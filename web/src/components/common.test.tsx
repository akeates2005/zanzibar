import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';

import { renderWithProviders } from '../test/render';

import { ActionButton, PermissionList, QueryBoundary } from './common';
import { ApiError } from '../api/problem';
import type { UseQueryResult } from '@tanstack/react-query';

describe('ActionButton', () => {
  it('is enabled and fires when the user holds the permission', async () => {
    const onClick = vi.fn();
    renderWithProviders(<ActionButton label="Rename" permission="edit" permissions={{ edit: true }} onClick={onClick} />);

    const button = screen.getByRole('button', { name: 'Rename' });
    expect(button).toBeEnabled();
    await userEvent.click(button);
    expect(onClick).toHaveBeenCalledOnce();
  });

  it('is disabled, not hidden, when the permission is missing, and the tooltip names it', async () => {
    const onClick = vi.fn();
    renderWithProviders(<ActionButton label="Top up" permission="top_up" permissions={{ view: true, top_up: false }} onClick={onClick} />);

    const button = screen.getByRole('button', { name: 'Top up' });
    expect(button).toBeDisabled();
    await userEvent.hover(button.closest('span') as HTMLElement);
    expect(await screen.findByText("Requires the 'top up' permission")).toBeInTheDocument();
    await userEvent.click(button);
    expect(onClick).not.toHaveBeenCalled();
  });

  it('treats a permission the server did not mention as not held', () => {
    renderWithProviders(<ActionButton label="Delete" permission="delete" permissions={{}} onClick={vi.fn()} />);
    expect(screen.getByRole('button', { name: 'Delete' })).toBeDisabled();
  });

  it('can be blocked for another reason even when the permission is held', async () => {
    renderWithProviders(<ActionButton label="Cancel" permission="cancel" permissions={{ cancel: true }} blockedReason="The subscription is already cancelled" onClick={vi.fn()} />);

    const button = screen.getByRole('button', { name: 'Cancel' });
    expect(button).toBeDisabled();
    await userEvent.hover(button.closest('span') as HTMLElement);
    expect(await screen.findByText('The subscription is already cancelled')).toBeInTheDocument();
  });
});

describe('PermissionList', () => {
  it('shows a tick or a cross for every permission and says so to a screen reader', () => {
    renderWithProviders(<PermissionList permissions={{ view: true, top_up: false }} />);

    const view = screen.getByTestId('permission-view');
    const topUp = screen.getByTestId('permission-top_up');
    expect(view).toHaveAttribute('data-held', 'true');
    expect(within(view).getByText('allowed')).toBeInTheDocument();
    expect(topUp).toHaveAttribute('data-held', 'false');
    expect(within(topUp).getByText('not allowed')).toBeInTheDocument();
    expect(within(topUp).getByText('top up')).toBeInTheDocument();
  });
});

describe('QueryBoundary', () => {
  const failed = (error: unknown) => ({ isPending: false, isError: true, error, data: undefined }) as unknown as UseQueryResult<string>;

  it('says "not found" for a 404 without revealing anything else', () => {
    renderWithProviders(<QueryBoundary query={failed(new ApiError(404, 'Not found', 'x'))} notFoundWhat="This wallet">{() => <p>data</p>}</QueryBoundary>);
    expect(screen.getByTestId('not-found')).toHaveTextContent('This wallet does not exist, or you do not have access to it.');
  });

  it('shows other errors as an alert', () => {
    renderWithProviders(<QueryBoundary query={failed(new ApiError(503, 'Unavailable', 'x'))}>{() => <p>data</p>}</QueryBoundary>);
    expect(screen.getByRole('alert')).toHaveTextContent(/try again/i);
  });

  it('shows data once it exists', () => {
    const ok = { isPending: false, isError: false, data: 'hello' } as unknown as UseQueryResult<string>;
    renderWithProviders(<QueryBoundary query={ok}>{(d) => <p>{d}</p>}</QueryBoundary>);
    expect(screen.getByText('hello')).toBeInTheDocument();
  });
});
