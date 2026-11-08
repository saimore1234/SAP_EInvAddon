# ERPNext API credential rotation

The API key/secret used during development were pasted into a chat session. Treat them as exposed and rotate them.

## Where credentials live (and do not)
* Add-on: `%LOCALAPPDATA%\EInvGstAddon\config.json` – key in clear, **secret DPAPI-encrypted for the Windows user** (`ApiSecretProtected`). Never in source control (`.gitignore` covers `config.json`), never in logs (the logger masks registered secrets).
* Tests: live tests read `EINV_KEY` / `EINV_SECRET` from the process environment only.
* A developer scratch file (outside the repo) held the old pair; delete it after rotation.

## Procedure (about 10 minutes, no add-on code change)
1. In ERPNext as an administrator: **User → (API user) → API Access → Generate Keys**. This issues a new key + secret and invalidates the old secret immediately (the old secret can no longer authenticate).
   Preferably create a dedicated user `sap-integration@…` with a restricted role (Sales Invoice / Customer / Item / Address / Contact / Warehouse create+read for company VCPL, Custom Field read) instead of reusing a personal user, then disable the old user's API access.
2. Copy the new secret once (it is shown only once) into the SAP client: **ERPNext Connection Settings → API Key / API Secret → Test Authentication → Save Configuration**. The secret field is write-only and is never displayed back.
3. Close the settings screen; restart the add-on (the sync engine is rebuilt automatically after Save).
4. Revoke the old credentials: on the old user, **Generate Keys** again (or remove API access) and confirm that a call with the old pair returns HTTP 401/403.
5. Delete any shell history / environment variables / scratch files holding the old values; unset `EINV_KEY`/`EINV_SECRET` in test shells.
6. Check `Error Logs`/`%LOCALAPPDATA%\EInvGstAddon\logs` – no secret should appear (masked as `****`).
7. Record date and operator of the rotation; rotate again on staff changes and at least every 90 days.

## Emergency (suspected leak)
Disable the API user in ERPNext first, then follow steps 1–7 with a new user.
