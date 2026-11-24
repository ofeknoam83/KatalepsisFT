import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { Store } from '@ngxs/store';
import { catchError, throwError } from 'rxjs';
import { SessionExpired } from './session.state';

/**
 * Marks every API call with the header the server requires on state-changing requests, and sends the user back to
 * the sign-in page when the server says the session has ended.
 */
export const apiInterceptor: HttpInterceptorFn = (request, next) => {
  if (!request.url.startsWith('/api/')) {
    return next(request);
  }

  const store = inject(Store);
  const router = inject(Router);
  return next(request.clone({ setHeaders: { 'X-KFT-Request': '1' } })).pipe(
    catchError((error: HttpErrorResponse) => {
      if (error.status === 401 && !request.url.startsWith('/api/auth/')) {
        store.dispatch(new SessionExpired());
        // A session that ran out: after signing in again the person is brought back to where they were.
        void router.navigate(['/login'], router.url.startsWith('/login') || router.url === '/' ? {} : { queryParams: { next: router.url } });
      }

      return throwError(() => error);
    }),
  );
};
