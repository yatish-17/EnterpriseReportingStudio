import {
  Bar,
  BarChart,
  CartesianGrid,
  Cell,
  Legend,
  Line,
  LineChart,
  Pie,
  PieChart,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from 'recharts';

type ChartType = 'Bar' | 'Line' | 'Pie';

interface ChartPoint {
  category: string;
  value: number;
}

const chartColors = ['#4e8b72', '#6689a0', '#c09658', '#8977a9', '#5e9a9a', '#b97268'];

function formatCategory(value: string) {
  const dateParts = /^(\d{4})-(\d{2})-\d{2}(?:T.*)?$/.exec(value);
  if (!dateParts) return value;
  const month = Number(dateParts[2]);
  const monthNames = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
  return month >= 1 && month <= 12 ? `${monthNames[month - 1]} ${dateParts[1]}` : value;
}

export default function ReportChartPreview({
  type,
  data,
  categoryName,
  valueName,
}: {
  type: ChartType;
  data: ChartPoint[];
  categoryName: string;
  valueName: string;
}) {
  const groupedData = [...data.reduce((groups, point) => {
    if (!Number.isFinite(point.value)) return groups;
    groups.set(point.category, (groups.get(point.category) ?? 0) + point.value);
    return groups;
  }, new Map<string, number>())].map(([category, value]) => ({ category: formatCategory(category), value }));

  if (data.length === 0) {
    return <div className="report-chart-empty" role="status">
      No report rows reached this chart. Check the dataset joins and report filters.
    </div>;
  }

  if (groupedData.length === 0) {
    return <div className="report-chart-empty" role="status">
      No numeric data is available for this chart. Check the selected value field and report filters.
    </div>;
  }

  return (
    <div className="report-chart-wrap" role="img" aria-label={`${type} chart of ${valueName} by ${categoryName}`}>
      <div className="report-chart-heading"><span>{type} chart</span><small>{valueName} by {categoryName}</small></div>
      <ResponsiveContainer width="100%" height={300} minWidth={0}>
        {type === 'Pie' ? (
          <PieChart>
            <Pie data={groupedData} nameKey="category" dataKey="value" outerRadius={100} label>
              {groupedData.map((row, index) => <Cell key={`${row.category}-${index}`} fill={chartColors[index % chartColors.length]} />)}
            </Pie>
            <Tooltip />
            <Legend />
          </PieChart>
        ) : type === 'Line' ? (
          <LineChart data={groupedData} margin={{ top: 10, right: 16, left: 0, bottom: 8 }}>
            <CartesianGrid stroke="#edf1ed" strokeDasharray="3 3" />
            <XAxis dataKey="category" tick={{ fontSize: 10, fill: '#74817a' }} />
            <YAxis tick={{ fontSize: 10, fill: '#74817a' }} />
            <Tooltip />
            <Line type="monotone" dataKey="value" name={valueName} stroke="#4e8b72" strokeWidth={2} dot={{ r: 3 }} />
          </LineChart>
        ) : (
          <BarChart data={groupedData} margin={{ top: 10, right: 16, left: 0, bottom: 8 }}>
            <CartesianGrid stroke="#edf1ed" strokeDasharray="3 3" />
            <XAxis dataKey="category" tick={{ fontSize: 10, fill: '#74817a' }} />
            <YAxis tick={{ fontSize: 10, fill: '#74817a' }} />
            <Tooltip />
            <Bar dataKey="value" name={valueName} fill="#4e8b72" radius={[3, 3, 0, 0]}>
              {groupedData.map((row, index) =>
                <Cell key={`${row.category}-${index}`} fill={chartColors[index % chartColors.length]} />)}
            </Bar>
          </BarChart>
        )}
      </ResponsiveContainer>
    </div>
  );
}
