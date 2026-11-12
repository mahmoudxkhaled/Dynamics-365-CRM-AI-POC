export interface CrmTableColumn {
  key: string;
  label: string;
  kind: string;
  logicalName?: string;
}

export interface CrmAggregatePresentation {
  totalGroupCount?: number;
  displayedGroupCount?: number;
  isTopN?: boolean;
  topNLimit?: number | null;
  primaryMetricAlias?: string | null;
  primaryMetricKind?: string | null;
  overallMetricTotal?: number | null;
  displayedMetricSubtotal?: number | null;
  displayedShareOfOverallPercent?: number | null;
  leadingGroupLabel?: string | null;
  leadingGroupMetric?: number | null;
  leadingGroupSharePercent?: number | null;
  groupNoun?: string;
  overallTotalKnown?: boolean;
}

export interface CrmStructuredData {
  entity?: string;
  operation?: string;
  resultMode?: 'complete' | 'partial' | string;
  limitation?: string | null;
  appliedFilters?: string;
  sort?: string;
  limit?: number;
  count?: number | null;
  countMayBeCapped?: boolean;
  /** Root entity primary id logical name — kept on records for identity; not shown by default. */
  primaryIdAttribute?: string | null;
  primaryNameAttribute?: string | null;
  columns?: CrmTableColumn[] | null;
  records?: Record<string, unknown>[] | null;
  aggregatePresentation?: CrmAggregatePresentation | null;
}

export interface ResolvedCell {
  value: unknown;
  kind?: string;
}
