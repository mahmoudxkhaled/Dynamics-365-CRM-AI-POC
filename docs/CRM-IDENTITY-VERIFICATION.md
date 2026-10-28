# CRM Windows Identity Verification

Verified locally on 2026-10-28 against ECRMBSS (`https://ecrmdev9.te.eg/ECRMBSS/api/data/v9.1/`).

## Findings

| Layer | Identity |
|-------|----------|
| Windows process (`whoami` / `WindowsIdentity.GetCurrent()`) | `CAIRO\Mahmoud.Essawy` (SID `S-1-5-21-...-297573`) |
| UPN | `mahmoud.essawy@te.eg` |
| CRM HTTP auth mode | `windows-default-credentials` (`UseDefaultCredentials` / `CredentialCache.DefaultCredentials`) |
| CRM `WhoAmI().UserId` | `b1335b28-556d-f111-afc3-005056b70b06` |
| CRM `systemuser.domainname` | `CAIRO\Mahmoud.Essawy` |
| CRM `systemuser.fullname` | `Mahmoud Khaled` |
| CRM `systemuser.internalemailaddress` | `Mahmoud.Essawy@te.eg` |
| CRM user disabled | `false` |
| Process account matches CRM domainname | **Yes** |

## Which identity is used for CRM?

For local `dotnet run` on your domain-joined machine:

1. The API process runs as **your interactive Windows account** (`CAIRO\Mahmoud.Essawy`).
2. Dynamics calls use **integrated Windows Authentication** via default credentials.
3. CRM maps that Windows account to the enabled `systemuser` above.
4. **No AD password is stored** in config for this path.
5. Incoming HTTP caller identity (browser → API) is **not** what CRM uses today unless the host impersonates the caller. On plain Kestrel HTTP, `HttpContext.User` is often unauthenticated.

Diagnostic endpoint: `GET /api/crm/diagnostics/identity`

## Groq / privacy

Unchanged: `Crm:AllowSendLiveDataToAi=false`. Live CRM rows are not sent to Groq.

## Multi-user deployment (requirements before implementing)

If many users will call the API and must each hit CRM as themselves:

1. **Host Windows Authentication** (IIS / HTTP.sys Negotiate/Kerberos), not anonymous.
2. **Impersonation + Kerberos constrained delegation** (or equivalent) so outbound CRM calls use the **caller** identity, not the app-pool/process identity.
3. **Per-user CRM authorization** remains CRM security roles + backend allowlists; never trust client-supplied roles.
4. Keep **read-only** Web API usage and privacy-safe AI tool execution.
5. Do **not** introduce a shared service account for interactive user queries if the goal is user-level CRM permissions.

Until those are designed, the POC correctly uses the developer’s Windows identity for CRM.
