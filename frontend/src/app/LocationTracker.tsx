/**
 * LocationTracker — side-effect-only component.
 *
 * Writes the PREVIOUS in-app location to sessionStorage('divoid.lastLocation')
 * on every navigation. Mounted inside AppRoutes (which is inside BrowserRouter)
 * so that useLocation() has router context.
 *
 * Algorithm:
 *  - prevRef holds the location from the last effect run.
 *  - When location changes, prevRef holds the route we just left — write it to
 *    sessionStorage before updating prevRef.
 *  - On first mount prevRef is null, so nothing is written (no prior location).
 *
 * NodeDetailPage.handleBack reads 'divoid.lastLocation' for its back button.
 * Using sessionStorage — not window.history.state.idx — means the same primitive
 * is in play in jsdom (tests) and the real browser. window.history.state.idx is a
 * react-router-dom 7 internal that is reliably set under MemoryRouter but NOT
 * under BrowserRouter in production (bug #388, PR #55 theatre-test trap).
 *
 * Bug #388.
 */

import { useEffect, useRef } from 'react';
import { useLocation } from 'react-router-dom';

export function LocationTracker() {
  const location = useLocation();
  const prevRef = useRef<string | null>(null);

  useEffect(() => {
    const current = location.pathname + location.search;
    if (prevRef.current && prevRef.current !== current) {
      sessionStorage.setItem('divoid.lastLocation', prevRef.current);
    }
    prevRef.current = current;
  }, [location.pathname, location.search]);

  return null;
}
