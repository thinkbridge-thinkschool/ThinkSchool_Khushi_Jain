import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { inject, provideAppInitializer } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';
import {
  provideRouter,
  withComponentInputBinding,
  withViewTransitions,
  type ActivatedRouteSnapshot,
} from '@angular/router';
import { AppComponent } from './app';
import {
  authInterceptor,
  errorMappingInterceptor,
  retryInterceptor,
  type AccessTokenResponse,
} from './http';
import { routes } from './routes';
import { SessionStore, refreshInterceptor } from './session';

// The transition belongs to the Day 16 pair, not to the tab bar. Every other
// tab keeps the instant switch it had before, and entering or leaving /routing
// is instant too -- only list <-> detail animates.
function isRoutedPair(snapshot: ActivatedRouteSnapshot): boolean {
  for (let node: ActivatedRouteSnapshot | null = snapshot; node; node = node.firstChild) {
    if (node.routeConfig?.path === 'routing') {
      return true;
    }
  }

  return false;
}

// The class this adds is what styles.css keys the animation off. Doing it this
// way rather than with transition.skipTransition() is deliberate: skipping
// rejects transition.ready, which the router itself console.errors in dev mode,
// so every tab switch would log 'AbortError: Transition was skipped'.
//
// Counted rather than a boolean, because a navigation starting before the
// previous transition has finished would otherwise have its class removed by
// the older one settling.
let routedTransitions = 0;

function animateRoutedPair(transition: ViewTransition): void {
  const root = document.documentElement;

  routedTransitions += 1;
  root.classList.add('routed-transition');

  const settled = () => {
    routedTransitions -= 1;

    if (routedTransitions === 0) {
      root.classList.remove('routed-transition');
    }
  };

  transition.finished.then(settled, settled);
}

interface DevSessionConfig {
  email?: string;
  password?: string;
}

interface GrantedDevSession {
  granted: AccessTokenResponse;
  email: string;
}

/**
 * Signs in once, before the app exists, so the pages that write never have to
 * ask. The credentials come from public/dev-session.json, which is not
 * committed -- config, not code. Every failure is answered with null rather
 * than an exception: the app still starts, the read-only pages still work, and
 * the guard sends anything that writes to /login.
 *
 * Fetched with plain fetch(), not HttpClient, because this runs before the
 * injector exists and must not pick up the interceptor chain.
 */
async function grantDevSession(): Promise<GrantedDevSession | null> {
  // Localhost only. A production build copies public/ into dist, so this guard
  // is what stops a deployed page from ever asking for the file or signing in
  // with what it finds.
  if (location.hostname !== 'localhost' && location.hostname !== '127.0.0.1') {
    return null;
  }

  try {
    const configResponse = await fetch('dev-session.json', { cache: 'no-store' });

    if (!configResponse.ok) {
      return null;
    }

    const config = (await configResponse.json()) as DevSessionConfig;

    if (!config.email || !config.password) {
      return null;
    }

    const loginResponse = await fetch('/api/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email: config.email, password: config.password }),
    });

    if (!loginResponse.ok) {
      console.warn(`Automatic sign-in refused with ${loginResponse.status}.`);

      return null;
    }

    return { granted: (await loginResponse.json()) as AccessTokenResponse, email: config.email };
  } catch (error) {
    console.warn('Automatic sign-in could not run.', error);

    return null;
  }
}

// Order is outermost first, and all four positions matter. Error mapping wraps
// everything, so it maps only what survives. Refresh sits above auth, so the
// request it replays has no Authorization header yet and picks up the new token
// on the way down. Retry is innermost, closest to the wire.
async function start(): Promise<void> {
  const devSession = await grantDevSession();

  await bootstrapApplication(AppComponent, {
    providers: [
      // withComponentInputBinding() is what lets the Day 16 detail page take the
      // route parameter as an input() instead of subscribing to paramMap, and
      // the routed list read ?page= the same way. No other routed component in
      // this app declares an input, so nothing else changes behaviour.
      provideRouter(
        routes,
        withComponentInputBinding(),
        withViewTransitions({
          onViewTransitionCreated: ({ transition, from, to }) => {
            if (isRoutedPair(from) && isRoutedPair(to)) {
              animateRoutedPair(transition);
            }
          },
        }),
      ),
      provideHttpClient(
        withInterceptors([
          errorMappingInterceptor,
          refreshInterceptor,
          authInterceptor,
          retryInterceptor,
        ]),
      ),
      // Runs before the router's first navigation, so the guard on the writing
      // pages sees a session that is already in place.
      provideAppInitializer(() => {
        if (devSession) {
          inject(SessionStore).store(devSession.granted, devSession.email);
        }
      }),
    ],
  });
}

start().catch((error) => console.error(error));
