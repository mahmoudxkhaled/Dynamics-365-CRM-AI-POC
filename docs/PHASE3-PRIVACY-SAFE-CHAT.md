# Phase 3 — Privacy-safe chat with live Dynamics

## Demonstrated end-to-end (2026-10-28)

### Arabic request
- Input: `هاتلي آخر 5 Sales Orders`
- Groq selected: `get_sales_orders` (arg `limit`)
- Outbound Groq audit: `roles=system,user`, `containsFunctionResults=False`, `suspiciousCrmText=False`, `safe=True`
- CRM provider: `Dynamics365`
- WhoAmI UserId: `b1335b28-556d-f111-afc3-005056b70b06`
- UI reply: local Arabic formatting of 5 real sales orders
- `outboundAiContainedCrmRecords`: **false**
- `privacySafeToolExecution`: **true**
- `Crm:AllowSendLiveDataToAi`: **false**

### English request
- Input: `Show me the latest 3 sales orders`
- Same privacy-safe path; 3 real orders returned locally

## Guarantees
- No `UseFunctionInvocation` middleware on AI clients
- Tool results are never appended as `FunctionResultContent` for a second Groq round-trip
- Conversation history sent to Groq is system + user/assistant text only
- Live CRM rows stay on the backend / Chat UI response payload
