import { inject } from '@angular/core';
import { CanActivateFn, Router, UrlTree } from '@angular/router';
import { Store } from '@ngxs/store';
import { Observable, map, of } from 'rxjs';
import { LoadSession, SessionState } from './session.state';

function withSession<T>(store: Store, then: () => T): Observable<T> {
  return store.selectSnapshot(SessionState.loaded) ? of(then()) : store.dispatch(new LoadSession()).pipe(map(then));
}

/** Pages inside the app: need a session, and an account that still has a given password may only change it. */
export const signedIn: CanActivateFn = (_route, state): Observable<boolean | UrlTree> => {
  const store = inject(Store);
  const router = inject(Router);
  return withSession(store, () => {
    const session = store.selectSnapshot(SessionState.session);
    if (session === null) {
      // The page that was asked for is remembered, so signing in leads there and not to the start page.
      return state.url === '/' ? router.parseUrl('/login') : router.createUrlTree(['/login'], { queryParams: { next: state.url } });
    }

    return session.mustChangePassword ? router.parseUrl('/change-password') : true;
  });
};

/** The change-password page: needs a session, nothing more. */
export const hasSession: CanActivateFn = (): Observable<boolean | UrlTree> => {
  const store = inject(Store);
  const router = inject(Router);
  return withSession(store, () => (store.selectSnapshot(SessionState.session) === null ? router.parseUrl('/login') : true));
};

/** The sign-in page: someone already signed in goes straight to the app. */
export const signedOut: CanActivateFn = (): Observable<boolean | UrlTree> => {
  const store = inject(Store);
  const router = inject(Router);
  return withSession(store, () => (store.selectSnapshot(SessionState.session) === null ? true : router.parseUrl('/')));
};

export const adminOnly: CanActivateFn = (): boolean | UrlTree => {
  const store = inject(Store);
  return store.selectSnapshot(SessionState.isAdmin) ? true : inject(Router).parseUrl('/');
};
