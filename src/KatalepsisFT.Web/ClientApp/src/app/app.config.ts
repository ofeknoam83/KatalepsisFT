import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { provideRouter } from '@angular/router';
import { provideStore } from '@ngxs/store';
import { en_US, provideNzI18n } from 'ng-zorro-antd/i18n';
import { routes } from './app.routes';
import { apiInterceptor } from './core/api.interceptor';
import { SessionState } from './core/session.state';
import { ViewState } from './funds/view.state';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZonelessChangeDetection(),
    provideRouter(routes),
    provideHttpClient(withInterceptors([apiInterceptor])),
    provideAnimationsAsync(),
    provideNzI18n(en_US),
    provideStore([SessionState, ViewState]),
  ],
};
