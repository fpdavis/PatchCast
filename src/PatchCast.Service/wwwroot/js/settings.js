// Connection-profile persistence via localStorage.
//
// SECURITY NOTE: unlike the WinForms client (which uses Windows DPAPI), the browser
// has no OS-backed secret store. Saved passwords are kept base64-encoded only to
// avoid casual shoulder-surfing; this is NOT encryption. Use only on trusted,
// localhost/dev machines, which is the supported scope for this client.

const STORAGE_KEY = 'patchcast.client.v1';

function load() {
    try {
        return JSON.parse(localStorage.getItem(STORAGE_KEY)) ?? {};
    } catch {
        return {};
    }
}

function save(state) {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(state));
}

export function listHosts() {
    return (load().hosts ?? []).map((profile) => profile.host);
}

export function getLastHost() {
    return load().lastHost ?? '';
}

export function findHost(host) {
    const key = (host ?? '').trim().toLowerCase();
    return (load().hosts ?? []).find((profile) => profile.host.toLowerCase() === key) ?? null;
}

export function saveProfile(profile) {
    const state = load();
    state.hosts = (state.hosts ?? []).filter((existing) => existing.host.toLowerCase() !== profile.host.toLowerCase());
    const stored = { ...profile };
    if (stored.savePassword && stored.password)
        stored.password = btoa(unescape(encodeURIComponent(stored.password)));
    else
        delete stored.password;
    state.hosts.push(stored);
    state.lastHost = profile.host;
    save(state);
}

export function loadProfile(host) {
    const stored = findHost(host);
    if (stored === null)
        return null;
    const profile = { ...stored };
    if (profile.savePassword && profile.password) {
        try {
            profile.password = decodeURIComponent(escape(atob(profile.password)));
        } catch {
            profile.password = '';
        }
    } else {
        profile.password = '';
    }
    return profile;
}

export function removeHost(host) {
    const state = load();
    const key = (host ?? '').trim().toLowerCase();
    state.hosts = (state.hosts ?? []).filter((profile) => profile.host.toLowerCase() !== key);
    if ((state.lastHost ?? '').toLowerCase() === key)
        state.lastHost = state.hosts[0]?.host ?? '';
    save(state);
}
