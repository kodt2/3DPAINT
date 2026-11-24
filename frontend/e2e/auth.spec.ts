import { expect, test } from '@playwright/test';
import { loginViaUi, registerViaUi, uniqueLogin } from './helpers';

/**
 * Step 3c.1 E2E: the SPA under /spa/** behind the Gateway.
 * Every test uses a fresh browser context (no shared cookies) and a unique
 * login, so tests never depend on each other or on leftover data.
 */
test('anonymous visitor on /spa/library is redirected to the login screen', async ({ page }) => {
  await page.goto('/spa/library');
  await expect(page).toHaveURL(/\/spa\/login/);
  await expect(page.getByRole('heading', { name: 'Вход' })).toBeVisible();
});

test('register, browse the library, logout, login again, logout', async ({ page }) => {
  const login = uniqueLogin();

  // Register lands directly in the library (the session cookie is set by
  // the register response).
  await registerViaUi(page, login);
  await expect(page).toHaveURL(/\/spa\/library/);
  await expect(page.getByText(`Здравствуйте, ${login}!`)).toBeVisible();

  // Logout drops the session: the library bounces back to the login.
  await page.getByRole('button', { name: 'Выйти' }).click();
  await expect(page).toHaveURL(/\/spa\/login/);
  await page.goto('/spa/library');
  await expect(page).toHaveURL(/\/spa\/login/);

  // The same credentials work through the login screen.
  await loginViaUi(page, login);
  await expect(page).toHaveURL(/\/spa\/library/);
  await expect(page.getByText(`Здравствуйте, ${login}!`)).toBeVisible();

  // Wrong password: the 401 ProblemDetails message is shown, no session.
  await page.getByRole('button', { name: 'Выйти' }).click();
  await expect(page).toHaveURL(/\/spa\/login/);
  await loginViaUi(page, login, 'wrong-password');
  await expect(page.getByRole('alert')).toContainText('Неверный логин или пароль');
  await expect(page).toHaveURL(/\/spa\/login/);
});

test('registering a taken login shows the 409 message', async ({ page }) => {
  const login = uniqueLogin();

  await registerViaUi(page, login);
  await expect(page).toHaveURL(/\/spa\/library/);

  // Log out and try to register the same login again.
  await page.getByRole('button', { name: 'Выйти' }).click();
  await expect(page).toHaveURL(/\/spa\/login/);

  await registerViaUi(page, login);
  await expect(page.getByRole('alert')).toContainText('Такой логин уже существует');
  await expect(page).toHaveURL(/\/spa\/register/);
});

test('short password is rejected with a per-field validation error', async ({ page }) => {
  const login = uniqueLogin();

  await page.goto('/spa/register');
  await page.getByLabel('Полное имя').fill('E2E Validation');
  await page.getByLabel('Дата рождения').fill('1995-01-15');
  await page.getByLabel('Логин').fill(login);
  await page.getByLabel('Пароль').fill('123');
  await page.getByRole('button', { name: 'Зарегистрироваться' }).click();

  await expect(page.getByRole('alert')).toContainText('Проверьте правильность заполнения полей.');
  await expect(page.getByText(/minimum length of 6/)).toBeVisible();
  await expect(page).toHaveURL(/\/spa\/register/);
});
