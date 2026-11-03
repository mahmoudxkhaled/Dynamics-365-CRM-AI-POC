export interface CrmTableColumn {
  key: string;
  label: string;
  kind: string;
  logicalName?: string;
}

export interface CrmStructuredData {
  entity?: string;
  operation?: string;
  appliedFilters?: string;
  sort?: string;
  limit?: number;
  count?: number | null;
  countMayBeCapped?: boolean;
  columns?: CrmTableColumn[] | null;
  records?: Record<string, unknown>[] | null;
}

export interface ResolvedCell {
  value: unknown;
  kind?: string;
}
