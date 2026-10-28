import { useMemo, useState } from 'react';

type SortDirection = 'ascending' | 'descending';

function compareValues(left: unknown, right: unknown) {
  if (left == null && right == null) return 0;
  if (left == null) return 1;
  if (right == null) return -1;
  if (typeof left === 'number' && typeof right === 'number') return left - right;
  if (typeof left === 'boolean' && typeof right === 'boolean') return Number(left) - Number(right);
  return String(left).localeCompare(String(right), undefined, { numeric: true, sensitivity: 'base' });
}

export function SortableReportTable({
  columns,
  rows,
  className,
  maxRows,
}: {
  columns: string[];
  rows: unknown[][];
  className?: string;
  maxRows?: number;
}) {
  const [sort, setSort] = useState<{ column: number; direction: SortDirection } | null>(null);
  const sortedRows = useMemo(() => {
    if (!sort) return rows;
    const multiplier = sort.direction === 'ascending' ? 1 : -1;
    return rows
      .map((row, index) => ({ row, index }))
      .sort((left, right) => {
        const leftValue = left.row[sort.column];
        const rightValue = right.row[sort.column];
        const comparison = leftValue == null || rightValue == null
          ? compareValues(leftValue, rightValue)
          : compareValues(leftValue, rightValue) * multiplier;
        return comparison || left.index - right.index;
      })
      .map(({ row }) => row);
  }, [rows, sort]);

  function toggleSort(column: number) {
    setSort((current) => current?.column === column
      ? { column, direction: current.direction === 'ascending' ? 'descending' : 'ascending' }
      : { column, direction: 'ascending' });
  }

  return (
    <table className={className}>
      <thead>
        <tr>{columns.map((column, index) => {
          const direction = sort?.column === index ? sort.direction : undefined;
          return <th key={`${column}-${index}`} aria-sort={direction}>
            <button
              type="button"
              className="report-table-sort"
              onClick={() => toggleSort(index)}
              aria-label={`Sort by ${column}${direction ? `, currently ${direction}` : ''}`}
            >
              {column}<span aria-hidden="true">{direction === 'ascending' ? ' ↑' : direction === 'descending' ? ' ↓' : ' ↕'}</span>
            </button>
          </th>;
        })}</tr>
      </thead>
      <tbody>{sortedRows.length === 0
        ? <tr><td colSpan={Math.max(columns.length, 1)}>No matching rows. Check dataset joins and report filters.</td></tr>
        : (maxRows === undefined ? sortedRows : sortedRows.slice(0, maxRows)).map((row, rowIndex) =>
          <tr key={rowIndex}>{columns.map((_, cellIndex) =>
            <td key={cellIndex}>{row[cellIndex] == null ? '—' : String(row[cellIndex])}</td>)}</tr>)}</tbody>
    </table>
  );
}
