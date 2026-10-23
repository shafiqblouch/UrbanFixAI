import type { ReactNode } from "react";

export interface Column<Row> {
  key: string;
  header: string;
  render: (row: Row) => ReactNode;
  /** Numbers align to the end of the line so they stay aligned in both directions. */
  numeric?: boolean;
}

export interface TableProps<Row> {
  caption: string;
  columns: Column<Row>[];
  rows: Row[];
  rowKey: (row: Row) => string;
}

export function Table<Row>({ caption, columns, rows, rowKey }: TableProps<Row>) {
  return (
    <div className="uf-table-wrap" tabIndex={0} role="region" aria-label={caption}>
      <table className="uf-table">
        <caption className="uf-sr-only">{caption}</caption>
        <thead>
          <tr>
            {columns.map((c) => (
              <th key={c.key} scope="col" className={c.numeric ? "uf-num" : undefined}>{c.header}</th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <tr key={rowKey(row)}>
              {columns.map((c) => (
                <td key={c.key} className={c.numeric ? "uf-num" : undefined}>{c.render(row)}</td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
