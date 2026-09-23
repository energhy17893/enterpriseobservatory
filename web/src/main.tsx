import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query'
import { BrowserRouter, Route, Routes } from 'react-router-dom'
import { ApiError, api } from '@/api/client'
import { Shell } from '@/components/Shell'
import { SignIn } from '@/routes/SignIn'
import { Setup } from '@/routes/Setup'
import { Overview } from '@/routes/Overview'
import { Alerts } from '@/routes/Alerts'
import { Events } from '@/routes/Events'
import { Entities } from '@/routes/Entities'
import { EntityDetail } from '@/routes/EntityDetail'
import { Simplivity } from '@/routes/Simplivity'
import { Collectors } from '@/routes/Collectors'
import { Accounts } from '@/routes/Accounts'
import { Connections } from '@/routes/Connections'
import { Maintenance } from '@/routes/Maintenance'
import { Compliance } from '@/routes/Compliance'
import { Reports } from '@/routes/Reports'
import { AlertsReport } from '@/routes/reports/AlertsReport'
import { Email } from '@/routes/Email'
import { ScheduledReports } from '@/routes/ScheduledReports'
import { ComplianceReport } from '@/routes/reports/ComplianceReport'
import { CapacityReport } from '@/routes/reports/CapacityReport'
import { ContinuityReport } from '@/routes/reports/ContinuityReport'
import { Appearance } from '@/routes/Appearance'
import { applyThemeSet, getThemeSet } from '@/lib/theme'
import './styles/index.css'

// Before the first render, so a viewer on another set never sees classic flash.
applyThemeSet(getThemeSet())

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      // Nothing is cached as fresh. Every screen in this product is a claim
      // about the present, and a stale one is the failure mode the whole
      // product exists to avoid — so refetching is the default and each screen
      // sets how often it needs to. See ADR-0007 §6.
      staleTime: 0,
      refetchOnWindowFocus: true,
      retry: 1,
    },
  },
})

/**
 * Nothing renders until the product knows who is asking.
 *
 * Reads are not harmless here: the estate's hostnames, addresses and serial
 * numbers are exactly what somebody would want before attacking it. So the gate
 * is in front of the whole application rather than around the buttons.
 */
function Application() {
  const { data, isPending, isError, error } = useQuery({
    queryKey: ['auth'],
    queryFn: api.authState,
    retry: false,
  })

  if (isPending) {
    return <div className="p-6 text-sm text-muted-foreground">Connecting…</div>
  }

  // 503 is what every endpoint answers while the installation has no database
  // (first-run setup, G-DB). The page that fixes that is the only one that
  // makes sense to show, whatever address was opened.
  if (isError && error instanceof ApiError && error.status === 503) {
    return <Setup />
  }

  if (isError || data === undefined) {
    return (
      <div className="p-6 text-sm text-status-warning-text">
        The server could not be reached. Nothing on this screen would be current anyway.
      </div>
    )
  }

  if (!data.signedIn) {
    return <SignIn state={data} />
  }

  return (
    <BrowserRouter>
      <Routes>
        <Route element={<Shell identity={data} />}>
          <Route index element={<Overview />} />
          <Route path="events" element={<Events />} />
          <Route path="alerts" element={<Alerts />} />
          <Route path="entities" element={<Entities />} />
          <Route path="entities/:id" element={<EntityDetail />} />
          <Route path="simplivity" element={<Simplivity />} />
          <Route path="collectors" element={<Collectors />} />
          <Route path="compliance" element={<Compliance identity={data} />} />
          <Route path="reports" element={<Reports />} />
          <Route path="reports/alerts" element={<AlertsReport />} />
          <Route path="reports/compliance" element={<ComplianceReport />} />
          <Route path="reports/capacity" element={<CapacityReport />} />
          <Route path="reports/continuity" element={<ContinuityReport />} />
          <Route path="maintenance" element={<Maintenance identity={data} />} />
          <Route path="connections" element={<Connections identity={data} />} />
          <Route path="reports/scheduled" element={<ScheduledReports identity={data} />} />
          <Route path="email" element={<Email identity={data} />} />
          <Route path="accounts" element={<Accounts identity={data} />} />
          <Route path="appearance" element={<Appearance />} />
        </Route>
      </Routes>
    </BrowserRouter>
  )
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <Application />
    </QueryClientProvider>
  </StrictMode>,
)
