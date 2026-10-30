import { LocaleProvider, type Locale } from "@urbanfix/ui";
import { useState, type ReactNode } from "react";
import { BrowserRouter, Navigate, Route, Routes } from "react-router-dom";
import { PeopleProvider } from "./components";
import { catalog, initialLocale } from "./i18n";
import { LiveProvider } from "./live";
import { LookupProvider } from "./lookups";
import { AuditPage } from "./pages/Audit";
import { ApprovalsPage } from "./pages/Approvals";
import { ComplaintDetailPage } from "./pages/ComplaintDetail";
import { ComplaintsPage } from "./pages/Complaints";
import { ConfigPage } from "./pages/Config";
import { DashboardPage } from "./pages/Dashboard";
import { JobDetailPage } from "./pages/JobDetail";
import { JobsPage } from "./pages/Jobs";
import { MapPage } from "./pages/MapPage";
import { LoginPage } from "./pages/Login";
import { NewComplaintPage } from "./pages/NewComplaint";
import { NoticesPage } from "./pages/Notices";
import { ReviewQueuePage } from "./pages/ReviewQueue";
import { StockPage } from "./pages/Stock";
import { ReportsPage } from "./pages/Reports";
import { ChargesPage } from "./pages/Charges";
import { DirectoryPage } from "./pages/Directory";
import { PlatformPage } from "./pages/Platform";
import { WelcomePage } from "./pages/Welcome";
import { homeFor, menusFor, type MenuId } from "./roles";
import { SessionProvider, useSession } from "./session";
import { Shell } from "./Shell";

/** A route only its roles may open; anyone else lands on their own home screen (WP-01). */
function Guard({ menu, children }: { menu: MenuId; children: ReactNode }) {
  const { role } = useSession();
  return menusFor(role).includes(menu) ? <>{children}</> : <Navigate to={homeFor(role)} replace />;
}

function Home() {
  const { role } = useSession();
  return <Navigate to={homeFor(role)} replace />;
}

function Routed({ onLocale }: { onLocale: (l: Locale) => void }) {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage onLocale={onLocale} />} />
      <Route element={<PeopleProvider><LookupProvider><LiveProvider><Shell onLocale={onLocale} /></LiveProvider></LookupProvider></PeopleProvider>}>
        <Route index element={<Home />} />
        <Route path="welcome" element={<WelcomePage />} />
        <Route path="dashboard" element={<Guard menu="dashboard"><DashboardPage /></Guard>} />
        <Route path="complaints" element={<Guard menu="complaints"><ComplaintsPage /></Guard>} />
        <Route path="complaints/:id" element={<Guard menu="complaints"><ComplaintDetailPage /></Guard>} />
        <Route path="map" element={<Guard menu="map"><MapPage /></Guard>} />
        <Route path="review" element={<Guard menu="review"><ReviewQueuePage /></Guard>} />
        <Route path="new" element={<Guard menu="new"><NewComplaintPage /></Guard>} />
        <Route path="jobs" element={<Guard menu="jobs"><JobsPage /></Guard>} />
        <Route path="jobs/:id" element={<Guard menu="jobs"><JobDetailPage /></Guard>} />
        <Route path="stock" element={<Guard menu="stock"><StockPage /></Guard>} />
        <Route path="approvals" element={<Guard menu="approvals"><ApprovalsPage /></Guard>} />
        <Route path="notices" element={<Guard menu="notices"><NoticesPage /></Guard>} />
        <Route path="config" element={<Guard menu="config"><ConfigPage /></Guard>} />
        <Route path="audit" element={<Guard menu="audit"><AuditPage /></Guard>} />
        <Route path="reports" element={<Guard menu="reports"><ReportsPage /></Guard>} />
        <Route path="charges" element={<Guard menu="charges"><ChargesPage /></Guard>} />
        <Route path="directory" element={<Guard menu="directory"><DirectoryPage /></Guard>} />
        <Route path="platform" element={<Guard menu="platform"><PlatformPage /></Guard>} />
        <Route path="*" element={<Home />} />
      </Route>
    </Routes>
  );
}

export function App() {
  const [locale, setLocale] = useState<Locale>(initialLocale);
  return (
    <LocaleProvider locale={locale} catalog={catalog} syncDocument>
      <SessionProvider>
        <BrowserRouter>
          <Routed onLocale={setLocale} />
        </BrowserRouter>
      </SessionProvider>
    </LocaleProvider>
  );
}
