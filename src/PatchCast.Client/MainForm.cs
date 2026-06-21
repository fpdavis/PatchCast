using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Diagnostics;
using PatchCast.Protocol;

namespace PatchCast.Client;

public sealed class MainForm : Form
{
    private static readonly int[] RetryDelaysSeconds = [0, 1, 2, 4, 8, 16, 32];

    private readonly TextBox hostText = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown portInput = new() { Minimum = 1, Maximum = 65535, Dock = DockStyle.Fill };
    private readonly TextBox passwordText = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
    private readonly CheckBox savePassword = new() { Text = "Save password securely for this Windows user", AutoSize = true };
    private readonly Button connectButton = new() { Text = "Connect", Dock = DockStyle.Fill };
    private readonly CheckBox systemMute = new() { Text = "Mute system audio", AutoSize = true };
    private readonly CheckBox micMute = new() { Text = "Mute microphone", AutoSize = true };
    private readonly Button showLogButton = new() { Text = "Show Log", AutoSize = true };
    private readonly Button forgetCertificateButton = new() { Text = "Forget Trusted Certificate", AutoSize = true };
    private readonly TrackBar systemVolume = NewVolumeControl();
    private readonly TrackBar micVolume = NewVolumeControl();
    private readonly Label connectionState = new() { Text = "Disconnected", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Label connectionQuality = new() { Text = "Not connected", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly AudioStreamPlayer systemPlayer = new();
    private readonly AudioStreamPlayer micPlayer = new();
    private readonly ClientSettings settings;
    private readonly ClientActivityLog activityLog = new();
    private CancellationTokenSource? sessionCancellation;
    private TcpClient? activeClient;
    private string currentRunPassword = string.Empty;
    private LogForm? logForm;

    public MainForm()
    {
        settings = ClientSettingsStore.Load();
        hostText.Text = settings.Host;
        portInput.Value = Math.Clamp(settings.Port, 1, 65535);
        var storedPassword = ClientSettingsStore.LoadPassword(settings);
        if (storedPassword is not null)
        {
            currentRunPassword = storedPassword;
            passwordText.Text = storedPassword;
            savePassword.Checked = true;
        }
        activityLog.Write("Client started.");
        if (storedPassword is not null)
            activityLog.Write("Loaded a DPAPI-protected saved password for the current Windows user.");

        Text = "PatchCast Client";
        ClientSize = new Size(700, 560);
        MinimumSize = new Size(716, 599);
        StartPosition = FormStartPosition.CenterScreen;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 2,
            RowCount = 12
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(layout, 0, "Server host or IP", hostText);
        AddRow(layout, 1, "TCP port", portInput);
        AddRow(layout, 2, "Server password", passwordText);
        AddRow(layout, 3, string.Empty, savePassword);
        AddRow(layout, 4, "System volume", systemVolume);
        AddRow(layout, 5, string.Empty, systemMute);
        AddRow(layout, 6, "Microphone volume", micVolume);
        AddRow(layout, 7, string.Empty, micMute);
        AddRow(layout, 8, "Status", connectionState);
        AddRow(layout, 9, "Connection quality", connectionQuality);
        var activityButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        activityButtons.Controls.Add(showLogButton);
        activityButtons.Controls.Add(forgetCertificateButton);
        AddRow(layout, 10, "Activity and errors", activityButtons);
        AddSpanningRow(layout, 11, connectButton);
        Controls.Add(layout);

        connectButton.Click += (_, _) => ToggleConnection();
        showLogButton.Click += (_, _) => ShowLog();
        forgetCertificateButton.Click += (_, _) => ForgetTrustedCertificate();
        systemVolume.ValueChanged += (_, _) => systemPlayer.SetVolume(systemVolume.Value / 100f);
        micVolume.ValueChanged += (_, _) => micPlayer.SetVolume(micVolume.Value / 100f);
        systemMute.CheckedChanged += (_, _) => systemPlayer.SetMuted(systemMute.Checked);
        micMute.CheckedChanged += (_, _) => micPlayer.SetMuted(micMute.Checked);
        FormClosing += (_, _) => RequestDisconnect();
    }

    private void ToggleConnection()
    {
        if (sessionCancellation is not null)
        {
            RequestDisconnect();
            return;
        }

        var host = hostText.Text.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            MessageBox.Show(this, "Enter a server host name or IP address.", "PatchCast", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        currentRunPassword = passwordText.Text;
        activityLog.Write($"Connect requested for {host}:{portInput.Value}.");
        settings.Host = host;
        settings.Port = (int)portInput.Value;
        try
        {
            ClientSettingsStore.SetPassword(settings, currentRunPassword, savePassword.Checked);
            ClientSettingsStore.Save(settings);
            activityLog.Write(savePassword.Checked
                ? "Password saved with Windows DPAPI for the current user."
                : "Password is retained in memory only for this run.");
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Client settings could not be saved: {exception.Message}", "PatchCast", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        sessionCancellation = new CancellationTokenSource();
        connectButton.Text = "Disconnect";
        SetConnectionInputsEnabled(false);
        _ = RunConnectionLoopAsync(host, (int)portInput.Value, currentRunPassword, sessionCancellation);
    }

    private async Task RunConnectionLoopAsync(
        string host,
        int port,
        string password,
        CancellationTokenSource session)
    {
        var retryIndex = 0;
        var attempt = 0;

        try
        {
            while (!session.IsCancellationRequested)
            {
                attempt++;
                connectionState.Text = "Connecting (now)";
                connectionQuality.Text = "Negotiating connection";
                activityLog.Write($"Connection attempt {attempt} to {host}:{port} started.");
                var authenticatedAt = DateTimeOffset.MinValue;
                var certificatePinMismatch = false;

                try
                {
                    using var client = new TcpClient { NoDelay = true };
                    activeClient = client;
                    await client.ConnectAsync(host, port, session.Token);
                    activityLog.Write($"TCP connected to {client.Client.RemoteEndPoint}.");

                    var pinKey = $"{host}:{port}";
                    string? observedPin = null;
                    using var secureStream = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
                    {
                        if (certificate is null)
                            return false;
                        observedPin = certificate.GetCertHashString(HashAlgorithmName.SHA256);
                        certificatePinMismatch = settings.CertificatePins.TryGetValue(pinKey, out var savedPin)
                            && !string.Equals(savedPin, observedPin, StringComparison.OrdinalIgnoreCase);
                        return !certificatePinMismatch;
                    });
                    await secureStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = host,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                    }, session.Token);
                    activityLog.Write($"TLS established: {secureStream.SslProtocol}, {secureStream.NegotiatedCipherSuite}.");

                    if (!await PasswordProtocol.AuthenticateClientAsync(secureStream, password, session.Token))
                        throw new UnauthorizedAccessException("The server rejected the password.");
                    activityLog.Write("Server password accepted.");

                    if (observedPin is not null && !settings.CertificatePins.ContainsKey(pinKey))
                    {
                        settings.CertificatePins[pinKey] = observedPin;
                        ClientSettingsStore.Save(settings);
                        activityLog.Write($"Trusted and pinned the server certificate SHA-256 fingerprint {observedPin}.");
                    }

                    authenticatedAt = DateTimeOffset.UtcNow;
                    connectionState.Text = "Connected";
                    var securityDescription = $"{secureStream.SslProtocol}, {secureStream.NegotiatedCipherSuite}";
                    connectionQuality.Text = $"Receiving audio; measuring quality ({securityDescription})";
                    activityLog.Write("Audio stream connected. Retry delay will reset after 32 stable seconds.");

                    using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                    var statistics = new ConnectionStatistics();
                    var receiveTask = ReceiveAsync(secureStream, statistics, connectionCancellation.Token);
                    var qualityTask = UpdateQualityAsync(statistics, securityDescription, connectionCancellation.Token);
                    try
                    {
                        var stableConnectionTask = Task.Delay(TimeSpan.FromSeconds(32), connectionCancellation.Token);
                        if (await Task.WhenAny(receiveTask, stableConnectionTask) == stableConnectionTask)
                        {
                            await stableConnectionTask;
                            retryIndex = 0;
                            activityLog.Write("Connection remained stable for 32 seconds; retry delay reset to zero.");
                        }
                        await receiveTask;
                    }
                    finally
                    {
                        connectionCancellation.Cancel();
                        try { await qualityTask; } catch (OperationCanceledException) { }
                    }
                }
                catch (OperationCanceledException) when (session.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    systemPlayer.Stop();
                    micPlayer.Stop();
                    activeClient = null;

                    if (authenticatedAt != DateTimeOffset.MinValue
                        && DateTimeOffset.UtcNow - authenticatedAt >= TimeSpan.FromSeconds(32))
                        retryIndex = 0;

                    connectionState.Text = "Disconnected";
                    connectionQuality.Text = "Not connected";
                    var delay = RetryDelaysSeconds[retryIndex];
                    retryIndex = Math.Min(retryIndex + 1, RetryDelaysSeconds.Length - 1);
                    var error = certificatePinMismatch
                        ? "The server certificate changed and was rejected. Disconnect, verify that the server was intentionally changed, then use Forget Trusted Certificate."
                        : FriendlyError(exception);
                    activityLog.Write($"{error} Exception: {exception.GetType().Name}: {exception.Message}");
                    activityLog.Write($"Next connection attempt in {delay} second(s).");
                    await ShowRetryCountdownAsync(delay, session.Token);
                }
                finally
                {
                    activeClient = null;
                }
            }
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
        }
        finally
        {
            systemPlayer.Stop();
            micPlayer.Stop();
            if (ReferenceEquals(sessionCancellation, session))
            {
                sessionCancellation = null;
                session.Dispose();
            }

            if (!IsDisposed)
            {
                connectionState.Text = "Disconnected";
                connectionQuality.Text = "Not connected";
                connectButton.Text = "Connect";
                SetConnectionInputsEnabled(true);
            }
        }
    }

    private async Task ShowRetryCountdownAsync(int delaySeconds, CancellationToken cancellationToken)
    {
        if (delaySeconds == 0)
        {
            connectionState.Text = "Connecting (now)";
            await Task.Yield();
            return;
        }

        for (var remaining = delaySeconds; remaining > 0; remaining--)
        {
            connectionState.Text = $"Connecting ({remaining}s)";
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private async Task ReceiveAsync(
        Stream stream,
        ConnectionStatistics statistics,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var packet = await AudioProtocol.ReadAsync(stream, cancellationToken);
            Interlocked.Increment(ref statistics.Packets);
            Interlocked.Add(ref statistics.Bytes, packet.Data.Length);
            if (packet.Channel == AudioChannel.SystemAudio)
                systemPlayer.Add(packet);
            else
                micPlayer.Add(packet);
        }
    }

    private void RequestDisconnect()
    {
        if (sessionCancellation is null)
            return;
        connectionState.Text = "Disconnecting";
        connectionQuality.Text = "Closing connection";
        activityLog.Write("Disconnect requested; automatic retries cancelled.");
        sessionCancellation.Cancel();
        activeClient?.Dispose();
    }

    private void SetConnectionInputsEnabled(bool enabled)
    {
        hostText.Enabled = enabled;
        portInput.Enabled = enabled;
        passwordText.Enabled = enabled;
        savePassword.Enabled = enabled;
        forgetCertificateButton.Enabled = enabled;
    }

    private void ForgetTrustedCertificate()
    {
        var host = hostText.Text.Trim();
        var pinKey = $"{host}:{(int)portInput.Value}";
        if (!settings.CertificatePins.ContainsKey(pinKey))
        {
            MessageBox.Show(this, $"No trusted certificate is saved for {pinKey}.", "PatchCast", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            $"Forget the trusted certificate for {pinKey}?\n\nOnly continue if you intentionally changed or reinstalled the server. The next password-authenticated connection will trust and pin the certificate presented by that server.",
            "PatchCast Certificate Trust",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirmation != DialogResult.Yes)
            return;

        settings.CertificatePins.Remove(pinKey);
        ClientSettingsStore.Save(settings);
        activityLog.Write($"Forgot the trusted server certificate for {pinKey} at the user's request.");
        MessageBox.Show(this, "The saved certificate was removed. You can now connect and pin the server's current certificate.", "PatchCast", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static string FriendlyError(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "Password rejected.",
        AuthenticationException => "TLS authentication failed.",
        SocketException => "Server unavailable.",
        EndOfStreamException => "Connection lost.",
        IOException => "Connection lost.",
        _ => $"Connection failed: {exception.Message}."
    };

    private async Task UpdateQualityAsync(
        ConnectionStatistics statistics,
        string securityDescription,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        long previousBytes = 0;
        long previousPackets = 0;
        var samples = 0;
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            var elapsed = stopwatch.Elapsed.TotalSeconds;
            stopwatch.Restart();
            var bytes = Interlocked.Read(ref statistics.Bytes);
            var packets = Interlocked.Read(ref statistics.Packets);
            var megabitsPerSecond = (bytes - previousBytes) * 8d / elapsed / 1_000_000d;
            var packetsPerSecond = (packets - previousPackets) / elapsed;
            previousBytes = bytes;
            previousPackets = packets;
            connectionQuality.Text = $"{megabitsPerSecond:F2} Mbit/s, {packetsPerSecond:F0} packets/s; {securityDescription}";

            if (++samples % 10 == 0)
                activityLog.Write($"Connection quality: {megabitsPerSecond:F2} Mbit/s, {packetsPerSecond:F0} audio packets/s; {securityDescription}.");
        }
    }

    private void ShowLog()
    {
        if (logForm is null || logForm.IsDisposed)
        {
            logForm = new LogForm(activityLog);
            logForm.Show(this);
        }
        else
        {
            logForm.Activate();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            RequestDisconnect();
            systemPlayer.Dispose();
            micPlayer.Dispose();
        }
        base.Dispose(disposing);
    }

    private static TrackBar NewVolumeControl() => new()
    {
        Minimum = 0,
        Maximum = 100,
        Value = 100,
        TickFrequency = 10,
        Dock = DockStyle.Fill
    };

    private static void AddRow(TableLayoutPanel panel, int row, string label, Control control)
    {
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 8.333f));
        panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        panel.Controls.Add(control, 1, row);
    }

    private static void AddRow(TableLayoutPanel panel, int row, Control left, Control right)
    {
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 8.333f));
        left.Anchor = AnchorStyles.Left;
        panel.Controls.Add(left, 0, row);
        panel.Controls.Add(right, 1, row);
    }

    private static void AddSpanningRow(TableLayoutPanel panel, int row, Control control)
    {
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 8.333f));
        panel.Controls.Add(control, 0, row);
        panel.SetColumnSpan(control, 2);
    }

    private sealed class ConnectionStatistics
    {
        public long Bytes;
        public long Packets;
    }
}
