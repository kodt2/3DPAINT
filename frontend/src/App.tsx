import { Route, Routes } from 'react-router';
import { ApiCheck } from './features/api-check/ApiCheck';

export default function App() {
  return (
    <Routes>
      <Route path="/" element={<Home />} />
      {/* Placeholder catch-all until the screens are migrated (Phase 3). */}
      <Route path="*" element={<Home />} />
    </Routes>
  );
}

function Home() {
  return (
    <main className="page">
      <h1>3DPAINT</h1>
      <p>SPA-заглушка (шаг 3a). Старый UI на Razor — под корнем, новая SPA — под /spa/.</p>
      <ApiCheck />
    </main>
  );
}
