import {
  columnClass,
  dropIdentityDisplayColumns,
  formatCell,
  niceHeader,
  resolveCellValue,
  resolveDisplayColumns
} from './crm-table.util';
import { CrmStructuredData, CrmTableColumn } from '../models/crm-table.model';

describe('crm-table.util formatting', () => {
  it('formats year without thousands separators', () => {
    expect(formatCell('year', 2025, 'year')).toBe('2025');
    expect(formatCell('year', 2025, 'integer')).not.toContain(',');
  });

  it('formats month numbers as month names', () => {
    expect(formatCell('month', 4, 'month')).toBe('April');
    expect(formatCell('month', 12, 'month')).toBe('December');
  });

  it('formats percentages with a percent sign', () => {
    expect(formatCell('pct', 33.33, 'percent')).toBe('33.33%');
    expect(formatCell('contribution', 50, 'percent')).toBe('50%');
  });

  it('uses friendly headers for aggregate aliases', () => {
    expect(niceHeader('year')).toBe('Year');
    expect(niceHeader('month')).toBe('Month');
    expect(niceHeader('record_count')).toBe('Count');
    expect(niceHeader('pct')).toBe('Contribution (%)');
  });

  it('renders null as an em dash', () => {
    expect(formatCell('count', null, 'integer')).toBe('—');
  });

  it('shows explicit uniqueidentifier values instead of blanking them', () => {
    const id = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
    expect(formatCell('omega_widgetid', id, 'uniqueidentifier')).toBe(id);
  });

  it('blanks raw lookup GUIDs without a display name', () => {
    expect(formatCell('customerid', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', 'lookup')).toBe('—');
  });
});

describe('crm-table.util alignment', () => {
  it('aligns money and integers as numeric columns', () => {
    expect(columnClass({ key: 'total', label: 'Total', kind: 'money' })).toBe('col-num');
    expect(columnClass({ key: 'cnt', label: 'Count', kind: 'integer' })).toBe('col-num');
  });

  it('aligns text and lookups left', () => {
    expect(columnClass({ key: 'name', label: 'Name', kind: 'string' })).toBe('col-text');
    expect(columnClass({ key: 'acct', label: 'Account', kind: 'lookup' })).toBe('col-text');
  });

  it('aligns datetime as date columns', () => {
    expect(columnClass({ key: 'createdon', label: 'Created', kind: 'datetime' })).toBe('col-date');
  });

  it('keeps numeric alignment for empty placeholders via type class', () => {
    expect(columnClass({ key: 'amount', label: 'Amount', kind: 'money' }, '—')).toBe('col-num');
  });
});

describe('crm-table.util identity columns', () => {
  it('drops primary id / uniqueidentifier columns for list display', () => {
    const cols: CrmTableColumn[] = [
      { key: 'name', label: 'Name', kind: 'string' },
      { key: 'omega_widgetid', label: 'omega_widgetid', kind: 'uniqueidentifier', logicalName: 'omega_widgetid' },
      { key: 'amount', label: 'Amount', kind: 'money' }
    ];
    const visible = dropIdentityDisplayColumns(cols, 'omega_widgetid');
    expect(visible.map((c) => c.key)).toEqual(['name', 'amount']);
  });

  it('hides inferred primary id from list structuredData even when present on records', () => {
    const structured: CrmStructuredData = {
      entity: 'omega_widget',
      operation: 'list',
      primaryIdAttribute: 'omega_widgetid',
      columns: [
        { key: 'name', label: 'Name', kind: 'string' },
        { key: 'omega_widgetid', label: 'omega_widgetid', kind: 'uniqueidentifier', logicalName: 'omega_widgetid' },
        { key: 'totalamount', label: 'Total Amount', kind: 'money' }
      ],
      records: [
        {
          name: 'W-1',
          omega_widgetid: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
          totalamount: 10
        }
      ]
    };
    const cols = resolveDisplayColumns(structured, structured.records as Record<string, unknown>[]);
    expect(cols.map((c) => c.key)).toEqual(['name', 'totalamount']);
  });

  it('prefers formatted lookup values over raw GUIDs', () => {
    const resolved = resolveCellValue(
      {
        customerid: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
        customerid_formatted: 'Acme شركة'
      },
      { key: 'customerid', label: 'Customer', kind: 'lookup', logicalName: 'customerid' }
    );
    expect(resolved.value).toBe('Acme شركة');
  });
});
