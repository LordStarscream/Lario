import { Routes } from '@angular/router';
import { authGuard } from './auth/auth.guard';
import { Login } from './auth/login';
import { ExpenseForm } from './form/expense-form';
import { MasterData } from './masterdata/master-data';
import { OpeningBalance } from './opening/opening-balance';
import { Shell } from './shell';
import { BookingsList } from './list/bookings-list';

export const routes: Routes = [
  { path: 'login', component: Login },
  {
    path: '',
    component: Shell,
    canActivate: [authGuard],
    children: [
      { path: '', pathMatch: 'full', component: BookingsList },
      { path: 'ausgaben/neu', component: ExpenseForm },
      { path: 'ausgaben/:id/korrektur', component: ExpenseForm },
      { path: 'anfangsbestaende', component: OpeningBalance },
      { path: 'stammdaten', component: MasterData },
    ],
  },
  { path: '**', redirectTo: '' },
];
