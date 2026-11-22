import { Signal, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, ParamMap } from '@angular/router';
import { Store } from '@ngxs/store';
import { Observable, catchError, combineLatest, of, switchMap, tap } from 'rxjs';
import { InstrumentFilter } from '../funds/models';
import { ViewState } from '../funds/view.state';

export interface PageData<T> {
  data: Signal<T | null>;
  failed: Signal<boolean>;
}

/** Loads a page's data again whenever its route parameters or the instrument filter change. */
export function pageData<T>(load: (params: ParamMap, filter: InstrumentFilter) => Observable<T>): PageData<T> {
  const route = inject(ActivatedRoute);
  const store = inject(Store);
  const failed = signal(false);
  const data = toSignal(
    combineLatest([route.paramMap, store.select(ViewState.filter)]).pipe(
      switchMap(([params, filter]) =>
        load(params, filter).pipe(
          tap(() => failed.set(false)),
          catchError(() => {
            failed.set(true);
            return of(null);
          }),
        ),
      ),
    ),
    { initialValue: null },
  );
  return { data, failed };
}
