import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { BrowserRouter } from 'react-router';
import App from './App';
import './index.css';

const queryClient = new QueryClient();

// basename "/spa" matches the Gateway route that serves this SPA
// (the /spa prefix is stripped before reaching nginx; React Router
// restores it client-side). Direct dev access uses /spa/ as well.
createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <BrowserRouter basename="/spa">
        <App />
      </BrowserRouter>
    </QueryClientProvider>
  </StrictMode>,
);
