import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { provideRouter, withNavigationErrorHandler } from '@angular/router';
import { provideStore } from '@ngxs/store';
import { provideNzConfig } from 'ng-zorro-antd/core/config';
import { en_US, provideNzI18n } from 'ng-zorro-antd/i18n';
import { routes } from './app.routes';
import { apiInterceptor } from './core/api.interceptor';
import { SessionState } from './core/session.state';
import { reloadWhenAppIsStale } from './core/stale-app';
import { ViewState } from './funds/view.state';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZonelessChangeDetection(),
    provideRouter(routes, withNavigationErrorHandler(reloadWhenAppIsStale)),
    provideHttpClient(withInterceptors([apiInterceptor])),
    provideAnimationsAsync(),
    provideNzI18n(en_US),
    // The brand colour for every ng-zorro component; the rest of the look is in styles.less.
    provideNzConfig({ theme: { primaryColor: '#4338ca' } }),
    provideStore([SessionState, ViewState]),
  ],
};
