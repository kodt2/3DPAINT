import { useLocation } from 'react-router';

/** Renders the current (memory) router pathname for navigation assertions. */
export function LocationProbe() {
  const location = useLocation();
  return <div data-testid="location">{location.pathname}</div>;
}
