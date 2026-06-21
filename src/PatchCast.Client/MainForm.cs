using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PatchCast.Protocol;

namespace PatchCast.Client;

public sealed class MainForm : Form
{
    private static readonly int[] RetryDelaysSeconds = [0, 1, 2, 4, 8, 16, 32];

    private readonly ComboBox hostInput = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly Button removeHostButton = new() { Text = "Remove", Dock = DockStyle.Fill };
    private readonly NumericUpDown portInput = new() { Minimum = 1, Maximum = 65535, Dock = DockStyle.Fill };
    private readonly TextBox passwordText = new() { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
    private readonly CheckBox savePassword = new() { AutoSize = true, AccessibleName = "Save Password" };
    private readonly Button connectButton = new() { Text = "Connect", Dock = DockStyle.Fill };
    private readonly CheckBox systemMute = new() { AutoSize = true, AccessibleName = "Mute System Audio" };
    private readonly CheckBox micMute = new() { AutoSize = true, AccessibleName = "Mute Microphone" };
    private readonly Button showLogButton = new() { Text = "Show Log", Dock = DockStyle.Fill };
    private readonly TrackBar systemVolume = NewVolumeControl();
    private readonly TrackBar micVolume = NewVolumeControl();
    private readonly Label connectionState = NewValueLabel("Disconnected");
    private readonly Label connectionQuality = NewValueLabel("Not Connected");
    private readonly ToolTip helpTips = new();
    private readonly AudioStreamPlayer systemPlayer = new();
    private readonly AudioStreamPlayer micPlayer = new();
    private readonly ClientSettings settings;
    private readonly Dictionary<string, string> currentRunPasswords = new(StringComparer.OrdinalIgnoreCase);
    private readonly ClientActivityLog activityLog = new();
    private CancellationTokenSource? sessionCancellation;
    private TcpClient? activeClient;
    private LogForm? logForm;
    private bool loadingHost;

    public MainForm()
    {
        settings = ClientSettingsStore.Load();

        Text = "PatchCast Client";
        ClientSize = new Size(650, 335);
        MinimumSize = new Size(560, 374);
        StartPosition = FormStartPosition.CenterScreen;

        helpTips.SetToolTip(savePassword, "Save this server password securely for the current Windows user using DPAPI.");
        helpTips.SetToolTip(systemMute, "Mute or unmute only the system-audio stream received from this server.");
        helpTips.SetToolTip(micMute, "Mute or unmute only the microphone stream received from this server.");
        helpTips.SetToolTip(removeHostButton, "Remove this host and all of its saved settings, password, and certificate trust.");

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            ColumnCount = 2,
            RowCount = 8
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(layout, 0, "Server Host Or IP:", CreateHostPanel(), 40);
        AddRow(layout, 1, "TCP Port:", portInput, 36);
        AddRow(layout, 2, "Server Password:", CreatePasswordPanel(), 36);
        AddRow(layout, 3, "System Volume:", CreateVolumePanel(systemMute, systemVolume), 48);
        AddRow(layout, 4, "Microphone Volume:", CreateVolumePanel(micMute, micVolume), 48);
        AddRow(layout, 5, "Status:", connectionState, 32);
        AddRow(layout, 6, "Connection Quality:", connectionQuality, 36);
        AddSpanningRow(layout, 7, CreateActionPanel(), 42);
        Controls.Add(layout);

        connectButton.Click += (_, _) => ToggleConnection();
        showLogButton.Click += (_, _) => ShowLog();
        removeHostButton.Click += (_, _) => RemoveCurrentHost();
        hostInput.SelectionChangeCommitted += (_, _) => LoadSelectedHost();
        hostInput.TextChanged += (_, _) =>
        {
            if (!loadingHost && sessionCancellation is null)
                removeHostButton.Enabled = settings.FindHost(hostInput.Text) is not null;
        };
        systemVolume.ValueChanged += (_, _) => systemPlayer.SetVolume(systemVolume.Value / 100f);
        micVolume.ValueChanged += (_, _) => micPlayer.SetVolume(micVolume.Value / 100f);
        systemMute.CheckedChanged += (_, _) => systemPlayer.SetMuted(systemMute.Checked);
        micMute.CheckedChanged += (_, _) => micPlayer.SetMuted(micMute.Checked);
        FormClosing += (_, _) => RequestDisconnect();

        RefreshHostItems(settings.LastHost);
        if (settings.FindHost(settings.LastHost ?? string.Empty) is { } initialProfile)
            LoadProfile(initialProfile);
        else
            ResetProfileControls();

        activityLog.Write($"Client started with {settings.Hosts.Count} saved host profile(s).");
    }

    private Control CreateHostPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        panel.Controls.Add(hostInput, 0, 0);
        panel.Controls.Add(removeHostButton, 1, 0);
        return panel;
    }

    private Control CreatePasswordPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        panel.Controls.Add(passwordText, 0, 0);
        panel.Controls.Add(savePassword, 1, 0);
        savePassword.Anchor = AnchorStyles.Left;
        return panel;
    }

    private static Control CreateVolumePanel(CheckBox mute, TrackBar volume)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        mute.Anchor = AnchorStyles.Left;
        panel.Controls.Add(mute, 0, 0);
        panel.Controls.Add(volume, 1, 0);
        return panel;
    }

    private Control CreateActionPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.Controls.Add(showLogButton, 0, 0);
        panel.Controls.Add(connectButton, 1, 0);
        return panel;
    }

    private void ToggleConnection()
    {
        if (sessionCancellation is not null)
        {
            RequestDisconnect();
            return;
        }

        var host = hostInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            MessageBox.Show(this, "Enter a server host name or IP address.", "PatchCast", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var password = passwordText.Text;
        var profile = settings.GetOrCreateHost(host);
        profile.Port = (int)portInput.Value;
        profile.SystemVolume = systemVolume.Value;
        profile.MuteSystemAudio = systemMute.Checked;
        profile.MicrophoneVolume = micVolume.Value;
        profile.MuteMicrophone = micMute.Checked;
        currentRunPasswords[host] = password;
        settings.LastHost = profile.Host;

        try
        {
            ClientSettingsStore.SetPassword(profile, password, savePassword.Checked);
            ClientSettingsStore.Save(settings);
            RefreshHostItems(profile.Host);
            activityLog.Write($"Saved connection profile for {profile.Host}:{profile.Port}.");
            activityLog.Write(savePassword.Checked
                ? "Password saved with Windows DPAPI for the current user."
                : "Password is retained in memory only for this run.");
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Client settings could not be saved: {exception.Message}", "PatchCast", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        activityLog.Write($"Connect requested for {profile.Host}:{profile.Port}.");
        sessionCancellation = new CancellationTokenSource();
        connectButton.Text = "Disconnect";
        SetConnectionInputsEnabled(false);
        _ = RunConnectionLoopAsync(profile, password, sessionCancellation);
    }

    private async Task RunConnectionLoopAsync(HostProfile profile, string password, CancellationTokenSource session)
    {
        var retryIndex = 0;
        var attempt = 0;
        var certificatePromptDeclined = false;

        try
        {
            while (!session.IsCancellationRequested)
            {
                attempt++;
                connectionState.Text = "Connecting (Now)";
                connectionQuality.Text = "Negotiating Connection";
                activityLog.Write($"Connection attempt {attempt} to {profile.Host}:{profile.Port} started.");
                var authenticatedAt = DateTimeOffset.MinValue;
                var certificatePinMismatch = false;
                string? observedPin = null;

                try
                {
                    using var client = new TcpClient { NoDelay = true };
                    activeClient = client;
                    await client.ConnectAsync(profile.Host, profile.Port, session.Token);
                    activityLog.Write($"TCP connected to {client.Client.RemoteEndPoint}.");

                    using var secureStream = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
                    {
                        if (certificate is null)
                            return false;
                        observedPin = certificate.GetCertHashString(HashAlgorithmName.SHA256);
                        certificatePinMismatch = !string.IsNullOrWhiteSpace(profile.CertificatePin)
                            && !string.Equals(profile.CertificatePin, observedPin, StringComparison.OrdinalIgnoreCase);
                        return !certificatePinMismatch;
                    });
                    await secureStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = profile.Host,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                    }, session.Token);
                    activityLog.Write($"TLS established: {secureStream.SslProtocol}, {secureStream.NegotiatedCipherSuite}.");

                    if (!await PasswordProtocol.AuthenticateClientAsync(secureStream, password, session.Token))
                        throw new UnauthorizedAccessException("The server rejected the password.");
                    activityLog.Write("Server password accepted.");

                    if (observedPin is not null && string.IsNullOrWhiteSpace(profile.CertificatePin))
                    {
                        profile.CertificatePin = observedPin;
                        ClientSettingsStore.Save(settings);
                        activityLog.Write($"Trusted and pinned the server certificate SHA-256 fingerprint {observedPin}.");
                    }

                    authenticatedAt = DateTimeOffset.UtcNow;
                    connectionState.Text = "Connected";
                    var securityDescription = $"{secureStream.SslProtocol}, {secureStream.NegotiatedCipherSuite}";
                    connectionQuality.Text = $"Receiving Audio; Measuring Quality ({securityDescription})";
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

                    var certificateRejected = certificatePinMismatch
                        || exception is AuthenticationException && !string.IsNullOrWhiteSpace(profile.CertificatePin);
                    if (certificateRejected && !certificatePromptDeclined)
                    {
                        activityLog.Write($"The server certificate was rejected. Presented fingerprint: {observedPin ?? "unavailable"}.");
                        if (PromptToForgetCertificate(profile))
                        {
                            profile.CertificatePin = null;
                            ClientSettingsStore.Save(settings);
                            retryIndex = 0;
                            activityLog.Write($"Forgot the trusted certificate for {profile.Host} at the user's request; reconnecting now.");
                        }
                        else
                        {
                            certificatePromptDeclined = true;
                            activityLog.Write("The user kept the existing trusted certificate.");
                        }
                    }

                    connectionState.Text = "Disconnected";
                    connectionQuality.Text = "Not Connected";
                    var delay = RetryDelaysSeconds[retryIndex];
                    retryIndex = Math.Min(retryIndex + 1, RetryDelaysSeconds.Length - 1);
                    var error = certificateRejected
                        ? "The server certificate was rejected."
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
                connectionQuality.Text = "Not Connected";
                connectButton.Text = "Connect";
                SetConnectionInputsEnabled(true);
            }
        }
    }

    private bool PromptToForgetCertificate(HostProfile profile)
    {
        var result = MessageBox.Show(
            this,
            $"The certificate presented by {profile.Host}:{profile.Port} does not match the trusted certificate.\n\nOnly forget it if you intentionally changed or reinstalled the server. The next password-authenticated connection will trust and pin the new certificate.\n\nForget the trusted certificate and reconnect?",
            "PatchCast Certificate Trust",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        return result == DialogResult.Yes;
    }

    private void LoadSelectedHost()
    {
        if (loadingHost || hostInput.SelectedItem is not string host)
            return;
        if (settings.FindHost(host) is { } profile)
            LoadProfile(profile);
    }

    private void LoadProfile(HostProfile profile)
    {
        loadingHost = true;
        try
        {
            hostInput.Text = profile.Host;
            portInput.Value = Math.Clamp(profile.Port, 1, 65535);
            var storedPassword = ClientSettingsStore.LoadPassword(profile);
            passwordText.Text = storedPassword
                ?? (currentRunPasswords.TryGetValue(profile.Host, out var runPassword) ? runPassword : string.Empty);
            savePassword.Checked = storedPassword is not null;
            systemVolume.Value = Math.Clamp(profile.SystemVolume, 0, 100);
            systemMute.Checked = profile.MuteSystemAudio;
            micVolume.Value = Math.Clamp(profile.MicrophoneVolume, 0, 100);
            micMute.Checked = profile.MuteMicrophone;
            removeHostButton.Enabled = sessionCancellation is null;
        }
        finally
        {
            loadingHost = false;
        }
    }

    private void ResetProfileControls()
    {
        loadingHost = true;
        try
        {
            hostInput.Text = string.Empty;
            portInput.Value = 4747;
            passwordText.Clear();
            savePassword.Checked = false;
            systemVolume.Value = 100;
            systemMute.Checked = false;
            micVolume.Value = 100;
            micMute.Checked = false;
            removeHostButton.Enabled = false;
        }
        finally
        {
            loadingHost = false;
        }
    }

    private void RefreshHostItems(string? selectedHost)
    {
        loadingHost = true;
        try
        {
            hostInput.Items.Clear();
            foreach (var profile in settings.Hosts.OrderBy(profile => profile.Host, StringComparer.OrdinalIgnoreCase))
                hostInput.Items.Add(profile.Host);
            if (!string.IsNullOrWhiteSpace(selectedHost))
                hostInput.Text = selectedHost;
        }
        finally
        {
            loadingHost = false;
        }
    }

    private void RemoveCurrentHost()
    {
        var host = hostInput.Text.Trim();
        var profile = settings.FindHost(host);
        if (profile is null)
        {
            MessageBox.Show(this, "Select a saved host to remove.", "PatchCast", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var result = MessageBox.Show(
            this,
            $"Remove {profile.Host} and all of its saved settings, password, and certificate trust?",
            "Remove PatchCast Host",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (result != DialogResult.Yes)
            return;

        settings.RemoveHost(profile.Host);
        currentRunPasswords.Remove(profile.Host);
        settings.LastHost = settings.Hosts.FirstOrDefault()?.Host;
        ClientSettingsStore.Save(settings);
        activityLog.Write($"Removed saved host profile {profile.Host}.");
        RefreshHostItems(settings.LastHost);
        if (settings.FindHost(settings.LastHost ?? string.Empty) is { } nextProfile)
            LoadProfile(nextProfile);
        else
            ResetProfileControls();
    }

    private async Task ShowRetryCountdownAsync(int delaySeconds, CancellationToken cancellationToken)
    {
        if (delaySeconds == 0)
        {
            connectionState.Text = "Connecting (Now)";
            await Task.Yield();
            return;
        }

        for (var remaining = delaySeconds; remaining > 0; remaining--)
        {
            connectionState.Text = $"Connecting ({remaining}s)";
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private async Task ReceiveAsync(Stream stream, ConnectionStatistics statistics, CancellationToken cancellationToken)
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
        connectionQuality.Text = "Closing Connection";
        activityLog.Write("Disconnect requested; automatic retries cancelled.");
        sessionCancellation.Cancel();
        activeClient?.Dispose();
    }

    private void SetConnectionInputsEnabled(bool enabled)
    {
        hostInput.Enabled = enabled;
        removeHostButton.Enabled = enabled && settings.FindHost(hostInput.Text) is not null;
        portInput.Enabled = enabled;
        passwordText.Enabled = enabled;
        savePassword.Enabled = enabled;
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

    private async Task UpdateQualityAsync(ConnectionStatistics statistics, string securityDescription, CancellationToken cancellationToken)
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
            connectionQuality.Text = $"{megabitsPerSecond:F2} Mbit/s, {packetsPerSecond:F0} Packets/s; {securityDescription}";

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
            helpTips.Dispose();
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

    private static Label NewValueLabel(string text) => new()
    {
        Text = text,
        AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft,
        Dock = DockStyle.Fill
    };

    private static void AddRow(TableLayoutPanel panel, int row, string label, Control control, int height)
    {
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        panel.Controls.Add(new Label
        {
            Text = label,
            Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleRight,
            Dock = DockStyle.Fill,
            Margin = new Padding(3, 3, 10, 3)
        }, 0, row);
        panel.Controls.Add(control, 1, row);
    }

    private static void AddSpanningRow(TableLayoutPanel panel, int row, Control control, int height)
    {
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        panel.Controls.Add(control, 0, row);
        panel.SetColumnSpan(control, 2);
    }

    private sealed class ConnectionStatistics
    {
        public long Bytes;
        public long Packets;
    }
}
