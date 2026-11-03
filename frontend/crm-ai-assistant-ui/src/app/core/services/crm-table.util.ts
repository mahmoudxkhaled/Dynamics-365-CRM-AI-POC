import { CrmStructuredData, CrmTableColumn, ResolvedCell } from '../models/crm-table.model';

const HIDDEN_KEYS = new Set([
  'salesorderid',
  'accountid',
  'contactid',
  'leadid',
  '@odata.etag',
  'odata.etag'
]);

const COLUMN_PRIORITY = [
  'name',
  'fullName',
  'fullname',
  'orderNumber',
  'ordernumber',
  'accountNumber',
  'accountnumber',
  'totalAmount',
  'totalamount',
  'createdOn',
  'createdon',
  'modifiedOn',
  'modifiedon',
  'stateLabel',
  'statusLabel',
  'statecode_label',
  'statuscode_label',
  'stateCode',
  'statusCode',
  'statecode',
  'statuscode',
  'emailAddress1',
  'emailaddress1',
  'telephone1',
  'jobTitle',
  'jobtitle',
  'city',
  'address1_city',
  'country',
  'address1_country',
  'customerAccountId',
  '_te_customeraccountid_value',
  '_customerid_value',
  '_parentcustomerid_value'
];

export function hasArabic(text: string | null | undefined): boolean {
  return /[\u0600-\u06FF]/.test(text || '');
}

export function isGuidLike(value: unknown): boolean {
  return (
    typeof value === 'string' &&
    /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/.test(
      value.trim()
    )
  );
}

export function formatCell(key: string, value: unknown, kind?: string): string {
  if (value === null || value === undefined || value === '') {
    return '—';
  }
  const kindNorm = String(kind || '').toLowerCase();
  if (
    ['lookup', 'customer', 'owner', 'uniqueidentifier', 'guid'].includes(kindNorm) ||
    isGuidLike(value)
  ) {
    if (isGuidLike(value) || (typeof value === 'string' && isGuidLike(String(value)))) {
      return '—';
    }
  }
  if (
    kindNorm === 'money' ||
    (typeof value === 'number' && /amount|price|revenue|total/i.test(key || ''))
  ) {
    const n = Number(value);
    if (!Number.isNaN(n)) {
      return n.toLocaleString(undefined, {
        minimumFractionDigits: 2,
        maximumFractionDigits: 2
      });
    }
  }
  if (kindNorm === 'datetime' || (typeof value === 'string' && /^\d{4}-\d{2}-\d{2}T/.test(value))) {
    try {
      return new Date(value as string).toISOString().slice(0, 10);
    } catch {
      /* fall through */
    }
  }
  if (kindNorm === 'boolean') {
    if (value === true || value === 'true') {
      return 'true';
    }
    if (value === false || value === 'false') {
      return 'false';
    }
  }
  if (typeof value === 'number') {
    return value.toLocaleString();
  }
  return String(value);
}

export function resolveCellValue(
  row: Record<string, unknown> | null | undefined,
  column: CrmTableColumn | null | undefined
): ResolvedCell {
  if (!row || !column) {
    return { value: null, kind: column?.kind };
  }
  const key = column.key;
  if (Object.prototype.hasOwnProperty.call(row, key) && row[key] != null && row[key] !== '') {
    return { value: row[key], kind: column.kind };
  }
  if (typeof key === 'string' && key.endsWith('_label')) {
    const raw = key.slice(0, -'_label'.length);
    if (Object.prototype.hasOwnProperty.call(row, raw)) {
      return { value: row[raw], kind: column.kind };
    }
  }
  if (typeof key === 'string' && key.endsWith('_formatted')) {
    const raw = key.slice(0, -'_formatted'.length);
    if (Object.prototype.hasOwnProperty.call(row, raw)) {
      return { value: row[raw], kind: column.kind };
    }
  }
  if (column.logicalName && Object.prototype.hasOwnProperty.call(row, column.logicalName)) {
    return { value: row[column.logicalName], kind: column.kind };
  }
  return { value: row[key], kind: column.kind };
}

export function niceHeader(key: string): string {
  const map: Record<string, string> = {
    name: 'Name',
    fullname: 'Full name',
    fullName: 'Full name',
    ordernumber: 'Order #',
    orderNumber: 'Order #',
    accountnumber: 'Account #',
    accountNumber: 'Account #',
    totalamount: 'Amount',
    totalAmount: 'Amount',
    createdon: 'Created',
    createdOn: 'Created',
    modifiedon: 'Modified',
    modifiedOn: 'Modified',
    statecode_label: 'State',
    statuscode_label: 'Status',
    stateLabel: 'State',
    statusLabel: 'Status',
    emailaddress1: 'Email',
    emailAddress1: 'Email',
    telephone1: 'Phone',
    jobtitle: 'Job title',
    jobTitle: 'Job title',
    address1_city: 'City',
    address1_country: 'Country'
  };
  return (
    map[key] ||
    key
      .replace(/^_/, '')
      .replace(/_value$/, '')
      .replace(/_label$/, '')
      .replaceAll('_', ' ')
  );
}

export function extractRows(structured: CrmStructuredData | null | undefined): Record<string, unknown>[] {
  if (!structured || typeof structured !== 'object') {
    return [];
  }
  return Array.isArray(structured.records) ? structured.records : [];
}

export function normalizeMetadataColumns(
  structured: CrmStructuredData | null | undefined
): CrmTableColumn[] | null {
  if (!structured || !Array.isArray(structured.columns) || structured.columns.length === 0) {
    return null;
  }
  return structured.columns
    .filter((c) => c && c.key)
    .map((c) => ({
      key: String(c.key),
      label: (c.label && String(c.label).trim()) || niceHeader(String(c.key)),
      kind: c.kind ? String(c.kind) : '',
      logicalName: c.logicalName ? String(c.logicalName) : ''
    }));
}

export function pickColumns(rows: Record<string, unknown>[]): CrmTableColumn[] {
  if (!rows.length) {
    return [];
  }
  const keys = new Set<string>();
  rows.forEach((row) =>
    Object.keys(row || {}).forEach((k) => {
      if (!HIDDEN_KEYS.has(k.toLowerCase()) && !k.startsWith('@') && !k.endsWith('_formatted')) {
        keys.add(k);
      }
    })
  );
  const preferred = COLUMN_PRIORITY.filter((k) => [...keys].some((x) => x.toLowerCase() === k));
  const resolvedPreferred = preferred
    .map((p) => [...keys].find((x) => x.toLowerCase() === p))
    .filter((x): x is string => Boolean(x));
  const rest = [...keys].filter(
    (k) => !resolvedPreferred.some((p) => p.toLowerCase() === k.toLowerCase())
  );
  const withoutRawCodes = rest.filter((k) => {
    const lower = k.toLowerCase();
    if (
      (lower === 'statecode' || lower === 'statuscode') &&
      [...keys].some((p) => p.toLowerCase() === lower + '_label')
    ) {
      return false;
    }
    return true;
  });
  return [...resolvedPreferred, ...withoutRawCodes].slice(0, 8).map((k) => ({
    key: k,
    label: niceHeader(k),
    kind: '',
    logicalName: k
  }));
}

export function columnClass(column: CrmTableColumn, sampleText: string): string {
  const kind = String(column.kind || '').toLowerCase();
  const key = `${column.key || ''} ${column.logicalName || ''}`;
  if (['money', 'integer', 'decimal'].includes(kind)) {
    return 'col-num';
  }
  if (['datetime', 'boolean', 'status', 'state', 'picklist'].includes(kind)) {
    return '';
  }
  if (/amount|price|revenue|total/i.test(key)) {
    return 'col-num';
  }
  if (
    /name|fullname|email|job|city|country|subject|description/i.test(key) ||
    kind === 'string' ||
    kind === 'memo'
  ) {
    return 'col-text';
  }
  if (sampleText === '—' || sampleText === '') {
    return 'col-empty';
  }
  return '';
}

function isLookupishColumn(c: CrmTableColumn): boolean {
  const kind = String(c.kind || '').toLowerCase();
  return (
    ['lookup', 'customer', 'owner', 'uniqueidentifier', 'guid'].includes(kind) ||
    /_value$/i.test(c.key || '') ||
    /customerid|parentcustomer/i.test(`${c.key || ''} ${c.logicalName || ''}`)
  );
}

export function dropAllEmptyLookupColumns(
  cols: CrmTableColumn[],
  rows: Record<string, unknown>[]
): CrmTableColumn[] {
  if (!rows.length) {
    return cols;
  }
  return cols.filter((c) => {
    if (!isLookupishColumn(c)) {
      return true;
    }
    return rows.some((row) => {
      const resolved = resolveCellValue(row, c);
      const text = formatCell(c.key, resolved.value, resolved.kind || c.kind);
      return text && text !== '—';
    });
  });
}

export function prioritizeDisplayColumns(cols: CrmTableColumn[]): CrmTableColumn[] {
  const score = (c: CrmTableColumn): number => {
    const k = `${c.key || ''} ${c.logicalName || ''}`.toLowerCase();
    if (/^(name|fullname|subject)$/.test((c.logicalName || c.key || '').toLowerCase())) {
      return 0;
    }
    if (/ordernumber|accountnumber/.test(k)) {
      return 1;
    }
    if (/createdon|modifiedon/.test(k)) {
      return 2;
    }
    if (/status|state/.test(k)) {
      return 3;
    }
    if (/amount|total|price/.test(k)) {
      return 4;
    }
    if (/email|telephone|phone|job/.test(k)) {
      return 5;
    }
    if (/_value$|customer|parent/.test(k)) {
      return 9;
    }
    return 6;
  };
  return [...cols].sort((a, b) => score(a) - score(b));
}

export function resolveDisplayColumns(
  structured: CrmStructuredData | null | undefined,
  rows: Record<string, unknown>[]
): CrmTableColumn[] {
  const metaCols = normalizeMetadataColumns(structured);
  let cols = metaCols && metaCols.length ? metaCols : pickColumns(rows);
  cols = dropAllEmptyLookupColumns(cols, rows);
  if (!cols.length) {
    return [];
  }
  return prioritizeDisplayColumns(cols);
}

export function summaryFromReply(reply: string | null | undefined): string {
  const lines = String(reply || '').split(/\r?\n/);
  return lines.filter((line) => !/^\s*\d+\.\s+/.test(line)).join('\n').trim();
}

export function isCountResult(
  structured: CrmStructuredData | null | undefined,
  rows: Record<string, unknown>[]
): boolean {
  return !!(
    structured &&
    (structured.operation === 'count' || structured.count != null) &&
    rows.length === 0
  );
}

export function shouldShowTable(
  structured: CrmStructuredData | null | undefined,
  rows: Record<string, unknown>[]
): boolean {
  return (
    rows.length > 0 ||
    !!(normalizeMetadataColumns(structured) && structured?.operation === 'list')
  );
}
