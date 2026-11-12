import { notifications } from '@mantine/notifications';
import { useMutation, useQueryClient } from '@tanstack/react-query';

import { useSession } from '../auth/AuthContext';

import type { Api } from './client';
import { userKey } from './keys';
import { describeError, unwrap } from './problem';
import type { GrantTargetType, ObjectType } from './types';

/** True when the server saved the change but could not yet tell the authorisation engine (HTTP 202). */
export function isPending(result: unknown): boolean {
  return typeof result === 'object' && result !== null && 'authorisationPending' in result && result.authorisationPending === true;
}

interface CommandOptions<V, R> {
  /** Shown as a success notification. */
  success: string | ((result: R, variables: V) => string);
  onSuccess?: (result: R, variables: V) => void;
}

/**
 * Wraps a command (a call that changes something). After every success it invalidates everything the signed-in user has
 * cached (the tree, the object, its access data, group and system views), which is the PRD's rule that every mutation
 * invalidates the tree, the affected object and its access data, done once so no screen can forget a part of it.
 * A 202 is reported honestly: the change is saved, but authorisation has not landed yet.
 */
export function useCommand<V, R>(run: (api: Api, variables: V) => Promise<R>, options: CommandOptions<V, R>) {
  const { user, api } = useSession();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (variables: V) => run(api, variables),
    onSuccess: async (result, variables) => {
      await queryClient.invalidateQueries({ queryKey: userKey(user.id) });
      const message = typeof options.success === 'function' ? options.success(result, variables) : options.success;
      if (isPending(result)) {
        notifications.show({
          color: 'yellow',
          title: 'Saved, authorisation pending',
          message: `${message} Access is still being applied and will appear shortly.`,
        });
      } else {
        notifications.show({ color: 'teal', message });
      }
      options.onSuccess?.(result, variables);
    },
    onError: (error) => {
      notifications.show({ color: 'red', title: 'Not done', message: describeError(error) });
    },
  });
}

// ---- Commerce ---------------------------------------------------------------------------------------------------

export const createCustomer = (api: Api, v: { name: string }) =>
  unwrap(api.POST('/api/customers', { body: { name: v.name } }));

export const createDomain = (api: Api, v: { customerId: string; name: string }) =>
  unwrap(api.POST('/api/customers/{id}/domains', { params: { path: { id: v.customerId } }, body: { name: v.name } }));

export const createWallet = (api: Api, v: { domainId: string; name: string }) =>
  unwrap(api.POST('/api/domains/{id}/wallets', { params: { path: { id: v.domainId } }, body: { name: v.name } }));

export const createSubscription = (api: Api, v: { walletId: string; productName: string; quantity: number }) =>
  unwrap(
    api.POST('/api/wallets/{id}/subscriptions', {
      params: { path: { id: v.walletId } },
      body: { productName: v.productName, quantity: v.quantity },
    }),
  );

export function renameObject(api: Api, v: { type: ObjectType; id: string; name: string }) {
  const request = { params: { path: { id: v.id } }, body: { name: v.name } };
  switch (v.type) {
    case 'customer':
      return unwrap(api.PATCH('/api/customers/{id}', request));
    case 'domain':
      return unwrap(api.PATCH('/api/domains/{id}', request));
    case 'wallet':
      return unwrap(api.PATCH('/api/wallets/{id}', request));
    case 'subscription':
      throw new Error('A subscription cannot be renamed.');
  }
}

export async function deleteObject(api: Api, v: { type: ObjectType; id: string }): Promise<{ authorisationPending: boolean }> {
  const path = { params: { path: { id: v.id } } };
  const request = () => {
    switch (v.type) {
      case 'customer':
        return api.DELETE('/api/customers/{id}', path);
      case 'domain':
        return api.DELETE('/api/domains/{id}', path);
      case 'wallet':
        return api.DELETE('/api/wallets/{id}', path);
      case 'subscription':
        return api.DELETE('/api/subscriptions/{id}', path);
    }
  };
  // 204 has no body; 202 carries the pending flag.
  return { authorisationPending: isPending(await unwrap<unknown>(request())) };
}

export const topUpWallet = (api: Api, v: { id: string; amount: number }) =>
  unwrap(api.POST('/api/wallets/{id}/top-up', { params: { path: { id: v.id } }, body: { amount: v.amount } }));

export const moveWallet = (api: Api, v: { id: string; targetDomainId: string }) =>
  unwrap(api.POST('/api/wallets/{id}/move', { params: { path: { id: v.id } }, body: { targetDomainId: v.targetDomainId } }));

export const changeQuantity = (api: Api, v: { id: string; quantity: number }) =>
  unwrap(api.POST('/api/subscriptions/{id}/quantity', { params: { path: { id: v.id } }, body: { quantity: v.quantity } }));

export const cancelSubscription = (api: Api, v: { id: string }) =>
  unwrap(api.POST('/api/subscriptions/{id}/cancel', { params: { path: { id: v.id } } }));

// ---- Access management ------------------------------------------------------------------------------------------

export interface SubjectChoice {
  type: 'user' | 'group';
  id: string;
}

export const grantAccess = (api: Api, v: { type: GrantTargetType; id: string; relation: string; subject: SubjectChoice }) =>
  unwrap(api.PUT('/api/access/{type}/{id}/grants', { params: { path: { type: v.type, id: v.id } }, body: { relation: v.relation, subject: v.subject } }));

export const revokeAccess = (api: Api, v: { type: GrantTargetType; id: string; relation: string; subject: SubjectChoice }) =>
  unwrap(
    api.DELETE('/api/access/{type}/{id}/grants', {
      params: { path: { type: v.type, id: v.id }, query: { relation: v.relation, subjectType: v.subject.type, subjectId: v.subject.id } },
    }),
  );

export const createGroup = (api: Api, v: { customerId: string | null; name: string }) =>
  v.customerId === null
    ? unwrap(api.POST('/api/platform/groups', { body: { name: v.name } }))
    : unwrap(api.POST('/api/customers/{id}/groups', { params: { path: { id: v.customerId } }, body: { name: v.name } }));

export async function deleteGroup(api: Api, v: { id: string }): Promise<{ authorisationPending: boolean }> {
  const body = await unwrap<unknown>(api.DELETE('/api/groups/{id}', { params: { path: { id: v.id } } }));
  return { authorisationPending: isPending(body) };
}

export const addGroupMember = (api: Api, v: { groupId: string; member: SubjectChoice }) =>
  unwrap(api.PUT('/api/groups/{id}/members', { params: { path: { id: v.groupId } }, body: v.member }));

export const removeGroupMember = (api: Api, v: { groupId: string; member: SubjectChoice }) =>
  unwrap(
    api.DELETE('/api/groups/{id}/members', {
      params: { path: { id: v.groupId }, query: { type: v.member.type, memberId: v.member.id } },
    }),
  );

// ---- System -----------------------------------------------------------------------------------------------------

export const reconcile = (api: Api, v: { repair: boolean }) =>
  unwrap(api.POST('/api/system/reconcile', { params: { query: { repair: v.repair } } }));

export const resetSystem = (api: Api) => unwrap(api.POST('/api/system/reset'));

export const explainAccess = (api: Api, v: { userId: string; permission: string; resourceType: string; resourceId: string }) =>
  unwrap(api.POST('/api/access/explain', { body: v }));
