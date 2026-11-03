import { Component, Input, OnChanges, SimpleChanges } from '@angular/core';
import { CrmStructuredData, CrmTableColumn } from '../../../../core/models/crm-table.model';
import {
  columnClass,
  extractRows,
  formatCell,
  resolveCellValue,
  resolveDisplayColumns
} from '../../../../core/services/crm-table.util';

interface TableCellView {
  text: string;
  cssClass: string;
}

interface TableRowView {
  cells: TableCellView[];
}

@Component({
  selector: 'app-crm-results-table',
  templateUrl: './crm-results-table.component.html',
  styleUrl: './crm-results-table.component.scss',
  standalone: false
})
export class CrmResultsTableComponent implements OnChanges {
  @Input() structuredData: CrmStructuredData | null | undefined;

  columns: CrmTableColumn[] = [];
  rows: TableRowView[] = [];
  empty = false;
  headerClasses: string[] = [];

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['structuredData']) {
      this.rebuild();
    }
  }

  private rebuild(): void {
    const records = extractRows(this.structuredData);
    const cols = resolveDisplayColumns(this.structuredData, records);
    this.columns = cols;
    this.headerClasses = cols.map((c) => columnClass(c, ''));
    this.empty = records.length === 0;

    if (!cols.length) {
      this.rows = [];
      return;
    }

    this.rows = records.map((row) => ({
      cells: cols.map((c) => {
        const resolved = resolveCellValue(row, c);
        const text = formatCell(c.key, resolved.value, resolved.kind || c.kind) || '—';
        return {
          text,
          cssClass: columnClass(c, text)
        };
      })
    }));
  }
}
