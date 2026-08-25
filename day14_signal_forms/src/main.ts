import { provideHttpClient } from '@angular/common/http';
import { bootstrapApplication } from '@angular/platform-browser';
import { SignalQuoteComponent } from './signal-quote';

bootstrapApplication(SignalQuoteComponent, {
  providers: [provideHttpClient()],
}).catch((error) => console.error(error));
