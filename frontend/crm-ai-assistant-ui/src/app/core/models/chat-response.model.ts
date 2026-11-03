import { CrmStructuredData } from './crm-table.model';

export interface ChatResponse {
  conversationId: string;
  reply: string;
  providerHint?: string | null;
  privacySafeToolExecution: boolean;
  selectedTool?: string | null;
  structuredData?: CrmStructuredData | null;
  crmWhoAmIUserId?: string | null;
  outboundAiContainedCrmRecords: boolean;
  crmDataProvider?: string | null;
}

export interface ChatErrorBody {
  error?: string;
  detail?: string;
  title?: string;
}

export type ChatMessageRole = 'user' | 'assistant' | 'meta';

export interface ChatUiMessage {
  id: string;
  role: ChatMessageRole;
  /** Plain text for user / meta / error / plain assistant replies. */
  text?: string;
  /** Full reply used when rendering structured assistant results. */
  reply?: string;
  structuredData?: CrmStructuredData | null;
  error?: boolean;
}
