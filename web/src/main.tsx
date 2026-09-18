import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { BrowserRouter, Route, Routes } from 'react-router-dom'
import { Shell } from '@/components/Shell'
import { Overview } from '@/routes/Overview'
import { Alerts } from '@/routes/Alerts'
import { Entities } from '@/routes/Entities'
import { EntityDetail } from '@/routes/EntityDetail'
import { Collectors } from '@/routes/Collectors'
import './styles/index.css'

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

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <Routes>
          <Route element={<Shell />}>
            <Route index element={<Overview />} />
            <Route path="alerts" element={<Alerts />} />
            <Route path="entities" element={<Entities />} />
            <Route path="entities/:id" element={<EntityDetail />} />
            <Route path="collectors" element={<Collectors />} />
          </Route>
        </Routes>
      </BrowserRouter>
    </QueryClientProvider>
  </StrictMode>,
)
