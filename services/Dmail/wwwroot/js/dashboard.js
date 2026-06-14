(function () {
    'use strict';

    const API_BASE = '';

    // ---- Initialization ----

    document.addEventListener('DOMContentLoaded', () => {
        handleAuthCallback();
        loadStatus();
        loadAccounts();
        setInterval(loadStatus, 5000); // Task 9.4: poll every 5s

        document.getElementById('add-account-btn').addEventListener('click', startOAuth2Flow);
    });

    // ---- Task 9.5: Check OAuth2 credentials ----

    function handleAuthCallback() {
        const params = new URLSearchParams(window.location.search);
        const auth = params.get('auth');
        if (auth === 'success') {
            window.history.replaceState({}, '', '/');
            setTimeout(loadAccounts, 500);
        } else if (auth === 'error') {
            window.history.replaceState({}, '', '/');
            showSetupWarning();
        }
    }

    function showSetupWarning() {
        document.getElementById('setup-warning').classList.remove('hidden');
    }

    // ---- API Calls ----

    async function apiGet(path) {
        const resp = await fetch(API_BASE + path);
        if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
        return resp.json();
    }

    async function apiPost(path, body) {
        const resp = await fetch(API_BASE + path, {
            method: 'POST',
            headers: body ? { 'Content-Type': 'application/json' } : {},
            body: body ? JSON.stringify(body) : undefined
        });
        if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
        if (resp.status === 202 || resp.status === 204) return null;
        const text = await resp.text();
        return text ? JSON.parse(text) : null;
    }

    async function apiDelete(path) {
        const resp = await fetch(API_BASE + path, { method: 'DELETE' });
        if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
    }

    // ---- Status Polling (Task 9.4) ----

    async function loadStatus() {
        try {
            const health = await apiGet('/health');
            const status = await apiGet('/api/status');

            // Display API key
            if (status.api_key) {
                document.getElementById('api-key').textContent = status.api_key;
            }

            const container = document.getElementById('status-content');
            const modelClass = health.model_loaded ? 'status-ok' : 'status-error';
            const dbClass = health.database_ok ? 'status-ok' : 'status-error';

            container.innerHTML = `
                <div class="status-item">
                    <span class="label">Model</span>
                    <span class="${modelClass}">${health.model_loaded ? 'Loaded' : 'Failed'}</span>
                </div>
                <div class="status-item">
                    <span class="label">Database</span>
                    <span class="${dbClass}">${health.database_ok ? 'Online' : 'Offline'}</span>
                </div>
                <div class="status-item">
                    <span class="label">Status</span>
                    <span class="status-${health.status === 'healthy' ? 'ok' : 'degraded'}">${health.status}</span>
                </div>
            `;
        } catch (e) {
            document.getElementById('status-content').innerHTML =
                '<p class="status-error">Unable to reach server</p>';
        }
    }

    // ---- Account Management ----

    async function loadAccounts() {
        try {
            const accounts = await apiGet('/api/accounts');
            const list = document.getElementById('account-list');
            const hasGoogleCreds = Object.keys(accounts).length > 0 || accounts.length > 0;

            if (!accounts || accounts.length === 0) {
                list.innerHTML = '<p class="empty">No accounts connected. Add your first Gmail account below.</p>';

                // Task 9.5: Show setup warning if Google creds not set
                // We can't check from the client side directly — server will indicate
                // via the auth flow. For now, show the button.
                return;
            }

            list.innerHTML = accounts.map(a => `
                <div class="account-card">
                    <div>
                        <div class="email">${escapeHtml(a.email)}</div>
                        <div class="meta">
                            <span class="state state-${a.state}">${a.state.replace(/_/g, ' ')}</span>
                            <span>${a.indexed_count || 0} emails</span>
                            <span>Last sync: ${a.last_sync || 'never'}</span>
                        </div>
                    </div>
                    <div class="actions">
                        <button onclick="window._syncAccount('${escapeHtml(a.email)}')">Sync Now</button>
                        <button class="danger" onclick="window._removeAccount('${escapeHtml(a.email)}')">Remove</button>
                    </div>
                </div>
            `).join('');
        } catch (e) {
            console.error('Failed to load accounts', e);
        }
    }

    function startOAuth2Flow() {
        window.location.href = API_BASE + '/api/auth/google/login';
    }

    window._syncAccount = async function (email) {
        try {
            await apiPost(`/api/accounts/${encodeURIComponent(email)}/sync`);
            setTimeout(loadAccounts, 1000);
        } catch (e) {
            alert('Sync failed: ' + e.message);
        }
    };

    window._removeAccount = async function (email) {
        if (!confirm(`Remove account ${email}? This will delete all associated emails, embeddings, and data.`)) {
            return;
        }
        try {
            await apiDelete(`/api/accounts/${encodeURIComponent(email)}`);
            loadAccounts();
        } catch (e) {
            alert('Remove failed: ' + e.message);
        }
    };

    // ---- API Key Display ----

    async function loadApiKey() {
        try {
            // The API key is auto-generated server-side.
            // We show instructions for finding it (logged at startup)
            document.getElementById('api-key').textContent = 'Auto-generated (check server logs)';
            document.getElementById('redirect-uri').textContent =
                window.location.origin + '/api/auth/google/callback';
        } catch (e) {
            // ignore
        }
    }

    loadApiKey();

    // ---- Utilities ----

    function escapeHtml(str) {
        const div = document.createElement('div');
        div.textContent = str;
        return div.innerHTML;
    }
})();
