import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { bootstrapApplication } from '@angular/platform-browser';
import {
  provideRouter,
  withComponentInputBinding,
  withViewTransitions,
  type ActivatedRouteSnapshot,
} from '@angular/router';
import { AppComponent } from './app';
import { authInterceptor, errorMappingInterceptor, retryInterceptor } from './http';
import { routes } from './routes';
import { DEV_CREDENTIALS, refreshInterceptor, type DevCredentials } from './session';

function isRoutedPair(snapshot: ActivatedRouteSnapshot): boolean {
  for (let node: ActivatedRouteSnapshot | null = snapshot; node; node = node.firstChild) {
    if (node.routeConfig?.path === 'routing') {
      return true;
    }
  }

  return false;
}

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

async function readDevCredentials(): Promise<DevCredentials | null> {
  if (location.hostname !== 'localhost' && location.hostname !== '127.0.0.1') {
    return null;
  }

  try {
    const response = await fetch('dev-session.json', { cache: 'no-store' });

    if (!response.ok) {
      return null;
    }

    const config = (await response.json()) as DevSessionConfig;

    return config.email && config.password
      ? { email: config.email, password: config.password }
      : null;
  } catch (error) {
    console.warn('Development credentials could not be read.', error);

    return null;
  }
}

async function start(): Promise<void> {
  const devCredentials = await readDevCredentials();

  await bootstrapApplication(AppComponent, {
    providers: [
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
      { provide: DEV_CREDENTIALS, useValue: devCredentials },
    ],
  });
}

start().catch((error) => console.error(error));
