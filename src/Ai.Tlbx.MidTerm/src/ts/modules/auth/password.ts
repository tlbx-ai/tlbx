/**
 * Password Modal Module
 *
 * Handles password set/change modal and form submission.
 */

import { changePassword } from '../../api/client';
import { $authStatus } from '../../stores';
import { bindClick } from '../../utils';
import { checkAuthStatus, dismissSecurityWarning, logout } from './status';
import { t } from '../i18n';
import { registerBackButtonLayer } from '../navigation/backButtonGuard';

let passwordModalHasPassword = false;
let releaseBackButtonLayer: (() => void) | null = null;

/**
 * Show the password modal for setting or changing password
 */
export function showPasswordModal(_isInitialSetup: boolean): void {
  const modal = document.getElementById('password-modal');
  const title = document.getElementById('password-modal-title');
  const currentGroup = document.getElementById('current-password-group');
  const errorEl = document.getElementById('password-error');

  if (!modal) return;

  passwordModalHasPassword = $authStatus.get()?.passwordSet ?? false;

  if (title) {
    title.textContent = passwordModalHasPassword
      ? t('modal.changePassword')
      : t('modal.setPassword');
  }

  if (currentGroup) {
    currentGroup.style.display = passwordModalHasPassword ? 'block' : 'none';
  }

  if (errorEl) {
    errorEl.classList.add('hidden');
    errorEl.textContent = '';
  }

  const form = document.getElementById('password-form') as HTMLFormElement | null;
  if (form) {
    form.reset();
  }

  if (!releaseBackButtonLayer) {
    releaseBackButtonLayer = registerBackButtonLayer(hidePasswordModal);
  }

  modal.classList.remove('hidden');

  const focusFieldId = passwordModalHasPassword ? 'current-password' : 'new-password';
  const field = document.getElementById(focusFieldId) as HTMLInputElement | null;
  if (field) {
    field.focus();
  }
}

/**
 * Hide the password modal
 */
function hidePasswordModal(): void {
  releaseBackButtonLayer?.();
  releaseBackButtonLayer = null;

  const modal = document.getElementById('password-modal');
  if (modal) {
    modal.classList.add('hidden');
  }
}

interface PasswordFormFields {
  currentPasswordInput: HTMLInputElement | null;
  newPasswordInput: HTMLInputElement | null;
  confirmPasswordInput: HTMLInputElement | null;
  saveButton: HTMLButtonElement | null;
}

function getPasswordFormFields(): PasswordFormFields {
  return {
    currentPasswordInput: document.getElementById('current-password') as HTMLInputElement | null,
    newPasswordInput: document.getElementById('new-password') as HTMLInputElement | null,
    confirmPasswordInput: document.getElementById('confirm-password') as HTMLInputElement | null,
    saveButton: document.getElementById('btn-save-password') as HTMLButtonElement | null,
  };
}

function validateNewPassword(newPassword: string, confirmPassword: string): string | null {
  if (!newPassword) {
    return t('error.newPasswordRequired');
  }

  if (newPassword !== confirmPassword) {
    return t('error.passwordsNoMatch');
  }

  if (newPassword.length < 10 || newPassword.length > 1024) {
    return t('error.passwordLength');
  }

  return null;
}

function setPasswordSaveButtonState(saveButton: HTMLButtonElement | null, pending: boolean): void {
  if (!saveButton) {
    return;
  }

  saveButton.disabled = pending;
  saveButton.textContent = pending ? t('modal.saving') : t('modal.save');
}

/**
 * Handle password form submission
 */
export async function handlePasswordSubmit(e: Event): Promise<void> {
  e.preventDefault();

  const fields = getPasswordFormFields();
  const newPassword = fields.newPasswordInput?.value ?? '';
  const confirmPassword = fields.confirmPasswordInput?.value ?? '';
  const validationError = validateNewPassword(newPassword, confirmPassword);
  if (validationError) {
    showPasswordError(validationError);
    return;
  }

  const currentPassword =
    passwordModalHasPassword && fields.currentPasswordInput
      ? fields.currentPasswordInput.value
      : null;
  setPasswordSaveButtonState(fields.saveButton, true);

  try {
    const { data, response } = await changePassword(currentPassword, newPassword);
    if (response.ok && data?.success) {
      hidePasswordModal();
      void checkAuthStatus();
    } else {
      showPasswordError(data?.error ?? t('error.passwordChangeFailed'));
    }
  } catch {
    showPasswordError(t('error.connectionError'));
  } finally {
    setPasswordSaveButtonState(fields.saveButton, false);
  }
}

/**
 * Show an error message in the password modal
 */
function showPasswordError(msg: string): void {
  const errorEl = document.getElementById('password-error');
  if (errorEl) {
    errorEl.textContent = msg;
    errorEl.classList.remove('hidden');
  }
}

/**
 * Bind all auth-related event handlers
 */
export function bindAuthEvents(): void {
  bindClick('btn-set-password-warning', () => {
    showPasswordModal(true);
  });
  bindClick('btn-dismiss-warning', dismissSecurityWarning);

  bindClick('btn-change-password', () => {
    showPasswordModal(false);
  });
  bindClick('btn-close-password', hidePasswordModal);
  bindClick('btn-cancel-password', hidePasswordModal);

  const passwordBackdrop = document.querySelector('#password-modal .modal-backdrop');
  if (passwordBackdrop) {
    passwordBackdrop.addEventListener('click', hidePasswordModal);
  }

  const passwordForm = document.getElementById('password-form');
  if (passwordForm) {
    passwordForm.addEventListener('submit', (e) => {
      void handlePasswordSubmit(e);
    });
  }

  bindClick('btn-logout', () => {
    void logout();
  });
}
