import { Anchor, Box, Breadcrumbs, Card, Group, List, Paper, SimpleGrid, Stack, Tabs, Text, Title } from '@mantine/core';
import { useState, type ReactNode } from 'react';
import { Link, useLocation, useNavigate, useParams } from 'react-router-dom';

import {
  cancelSubscription,
  changeQuantity,
  createDomain,
  createSubscription,
  createWallet,
  deleteObject,
  moveWallet,
  renameObject,
  topUpWallet,
  useCommand,
} from '../../api/commands';
import { useChildren, useObject, useTree } from '../../api/queries';
import { isObjectType, type BreadcrumbDto, type ObjectDto, type ObjectType } from '../../api/types';
import { ActionButton, EmptyState, NotFoundPanel, PageTitle, PermissionList, QueryBoundary } from '../../components/common';
import { formatDateTime, formatMoney, typeLabel } from '../../lib/format';
import { moveTargets } from '../../lib/tree';
import { AccessTab } from '../access/AccessTab';
import { PlatformPage } from '../platform/PlatformPage';
import { AmountModal, ConfirmModal, MoveWalletModal, QuantityModal, SubscriptionModal, TextPromptModal } from './dialogs';

type Dialog = 'rename' | 'create' | 'topup' | 'move' | 'subscription' | 'quantity' | 'delete' | null;

const CHILD_LABEL: Partial<Record<ObjectType, string>> = { customer: 'Domains', domain: 'Wallets', wallet: 'Subscriptions' };

export const linkTo = (type: string, id: string): string => `/resources/${type}/${id}`;

/** Routes `/resources/:type/:id` and `/resources/:type/:id/access`. */
export function ResourceDetail() {
  const { type, id } = useParams();
  if (type === 'platform') return <PlatformPage />;
  if (!isObjectType(type) || !id) return <NotFoundPanel />;
  return <ObjectDetail key={`${type}:${id}`} type={type} id={id} />;
}

function Crumbs({ ancestors }: { ancestors: readonly BreadcrumbDto[] }) {
  if (ancestors.length === 0) return null;
  return (
    <Box component="nav" aria-label="Where this is" mb="xs">
      <Breadcrumbs>
      {ancestors.map((a) =>
        a.viewable && a.id !== null ? (
          <Anchor key={`${a.type}:${a.id}`} component={Link} to={linkTo(a.type, a.id)} size="sm">
            {a.name}
          </Anchor>
        ) : (
          // Names only for what the user cannot open, never a link.
          <Text key={`${a.type}~${a.name}`} size="sm" c="dimmed" title="You cannot open this">
            {a.name}
          </Text>
        ),
      )}
      </Breadcrumbs>
    </Box>
  );
}

function fields(object: ObjectDto, type: ObjectType): [string, ReactNode][] {
  const common: [string, ReactNode][] = [
    ['Created', formatDateTime(object.createdAt)],
    ['Created by', object.createdBy],
    ['Version', String(object.rowVersion)],
  ];
  switch (type) {
    case 'customer':
    case 'domain':
      return [['Name', (object as { name: string }).name], ...common];
    case 'wallet': {
      const w = object as { name: string; balance: number };
      return [['Name', w.name], ['Balance', <span key="b" data-testid="wallet-balance">{formatMoney(w.balance)}</span>], ...common];
    }
    case 'subscription': {
      const s = object as { productName: string; quantity: number; status: string };
      return [
        ['Product', s.productName],
        ['Quantity', <span key="q" data-testid="subscription-quantity">{s.quantity}</span>],
        ['Status', <span key="s" data-testid="subscription-status">{s.status}</span>],
        ...common,
      ];
    }
  }
}

function nameOf(object: ObjectDto): string {
  return 'productName' in object ? object.productName : object.name;
}

function ObjectDetail({ type, id }: { type: ObjectType; id: string }) {
  const query = useObject(type, id);
  return (
    <QueryBoundary query={query} notFoundWhat={`This ${type}`}>
      {(object) => <ObjectView type={type} id={id} object={object} />}
    </QueryBoundary>
  );
}

function ObjectView({ type, id, object }: { type: ObjectType; id: string; object: ObjectDto }) {
  const location = useLocation();
  const navigate = useNavigate();
  const onAccessTab = location.pathname.endsWith('/access');
  const canManageAccess = object.permissions.manage_access === true;
  const name = nameOf(object);

  return (
    <Stack>
      <Crumbs ancestors={object.ancestors} />
      <PageTitle title={name} badge={typeLabel(type)} actions={<Actions type={type} id={id} object={object} />} />
      <Tabs
        value={onAccessTab && canManageAccess ? 'access' : 'details'}
        onChange={(value) => { void navigate(value === 'access' ? `${linkTo(type, id)}/access` : linkTo(type, id)); }}
        keepMounted={false}
      >
        <Tabs.List>
          <Tabs.Tab value="details">Details</Tabs.Tab>
          {canManageAccess ? <Tabs.Tab value="access">Access</Tabs.Tab> : null}
        </Tabs.List>
        <Tabs.Panel value="details" pt="md">
          <Details type={type} id={id} object={object} />
        </Tabs.Panel>
        {canManageAccess ? (
          <Tabs.Panel value="access" pt="md">
            <AccessTab type={type} id={id} permissions={object.permissions} ancestors={object.ancestors} />
          </Tabs.Panel>
        ) : null}
      </Tabs>
    </Stack>
  );
}

function Details({ type, id, object }: { type: ObjectType; id: string; object: ObjectDto }) {
  return (
    <SimpleGrid cols={{ base: 1, md: 2 }} spacing="lg" verticalSpacing="lg">
      <Stack>
        <Paper withBorder p="md">
          <Title order={5} mb="xs">
            Details
          </Title>
          <Stack gap={4}>
            {fields(object, type).map(([label, value]) => (
              <Group key={label} gap="xs" wrap="nowrap" align="flex-start">
                <Text span fw={600} w={110}>
                  {label}
                </Text>
                <Text span>{value}</Text>
              </Group>
            ))}
          </Stack>
        </Paper>
        <ChildList type={type} id={id} />
        {type === 'customer' ? (
          <Anchor component={Link} to={`/customers/${id}/groups`}>
            Groups of this customer
          </Anchor>
        ) : null}
      </Stack>
      <Card withBorder padding="md">
        <Title order={5} mb="xs">
          Your access
        </Title>
        <PermissionList permissions={object.permissions} />
      </Card>
    </SimpleGrid>
  );
}

function ChildList({ type, id }: { type: ObjectType; id: string }) {
  const label = CHILD_LABEL[type];
  const children = useChildren(type, id, label !== undefined);
  if (!label) return null;
  return (
    <Paper withBorder p="md">
      <Title order={5} mb="xs">
        {label}
      </Title>
      <QueryBoundary query={children}>
        {(items) =>
          items.length === 0 ? (
            <EmptyState title={`No ${label.toLowerCase()} you can see`} />
          ) : (
            <List listStyleType="none" spacing={2} withPadding={false} aria-label={label}>
              {items.map((c) => (
                <List.Item key={c.id}>
                  <Anchor component={Link} to={linkTo(c.type, c.id)}>
                    {c.name}
                  </Anchor>
                </List.Item>
              ))}
            </List>
          )
        }
      </QueryBoundary>
    </Paper>
  );
}

/** The action set for an object type. Each action is enabled by the permission the server requires for it. */
function Actions({ type, id, object }: { type: ObjectType; id: string; object: ObjectDto }) {
  const navigate = useNavigate();
  const tree = useTree();
  const [dialog, setDialog] = useState<Dialog>(null);
  const close = () => { setDialog(null); };
  const permissions = object.permissions;
  const name = nameOf(object);

  const rename = useCommand(renameObject, { success: 'Renamed.', onSuccess: close });
  const remove = useCommand(deleteObject, {
    success: `${name} deleted.`,
    onSuccess: () => {
      close();
      const parent = [...object.ancestors].reverse().find((a) => a.viewable && a.id !== null && a.type !== 'platform');
      void navigate(parent?.id ? linkTo(parent.type, parent.id) : '/resources');
    },
  });
  const addDomain = useCommand(createDomain, { success: 'Domain added.', onSuccess: close });
  const addWallet = useCommand(createWallet, { success: 'Wallet added.', onSuccess: close });
  const topUp = useCommand(topUpWallet, { success: 'Wallet topped up.', onSuccess: close });
  const move = useCommand(moveWallet, { success: 'Wallet moved.', onSuccess: close });
  const addSubscription = useCommand(createSubscription, { success: 'Subscription added.', onSuccess: close });
  const setQuantity = useCommand(changeQuantity, { success: 'Quantity changed.', onSuccess: close });
  const cancel = useCommand(cancelSubscription, { success: 'Subscription cancelled.' });

  const cancelled = 'status' in object && object.status === 'Cancelled';
  const cancelledReason = cancelled ? 'The subscription is already cancelled' : undefined;
  const walletDomainId = 'domainId' in object ? object.domainId : '';

  return (
    <>
      <Group gap="xs" role="toolbar" aria-label="Actions">
        {type !== 'subscription' ? <ActionButton variant="default" label="Rename" permission="edit" permissions={permissions} onClick={() => { setDialog('rename'); }} /> : null}
        {type === 'customer' ? <ActionButton label="Add domain" permission="create_domain" permissions={permissions} onClick={() => { setDialog('create'); }} /> : null}
        {type === 'domain' ? <ActionButton label="Add wallet" permission="create_wallet" permissions={permissions} onClick={() => { setDialog('create'); }} /> : null}
        {type === 'wallet' ? (
          <>
            <ActionButton label="Top up" permission="top_up" permissions={permissions} onClick={() => { setDialog('topup'); }} />
            <ActionButton variant="default" label="Move to another domain" permission="move" permissions={permissions} onClick={() => { setDialog('move'); }} />
            <ActionButton label="Add subscription" permission="create_subscription" permissions={permissions} onClick={() => { setDialog('subscription'); }} />
          </>
        ) : null}
        {type === 'subscription' ? (
          <>
            <ActionButton label="Change quantity" permission="change_quantity" permissions={permissions} blockedReason={cancelledReason} onClick={() => { setDialog('quantity'); }} />
            <ActionButton variant="default" label="Cancel subscription" permission="cancel" permissions={permissions} blockedReason={cancelledReason} onClick={() => { cancel.mutate({ id }); }} />
          </>
        ) : null}
        <ActionButton color="red" variant="outline" label="Delete" permission="delete" permissions={permissions} onClick={() => { setDialog('delete'); }} />
      </Group>

      <TextPromptModal opened={dialog === 'rename'} onClose={close} busy={rename.isPending} title={`Rename ${typeLabel(type).toLowerCase()}`} label="New name" initial={name} submitLabel="Rename" onSubmit={(value) => { rename.mutate({ type, id, name: value }); }} />
      <TextPromptModal opened={dialog === 'create' && type === 'customer'} onClose={close} busy={addDomain.isPending} title="Add domain" label="Domain name" submitLabel="Add domain" onSubmit={(value) => { addDomain.mutate({ customerId: id, name: value }); }} />
      <TextPromptModal opened={dialog === 'create' && type === 'domain'} onClose={close} busy={addWallet.isPending} title="Add wallet" label="Wallet name" submitLabel="Add wallet" onSubmit={(value) => { addWallet.mutate({ domainId: id, name: value }); }} />
      <AmountModal opened={dialog === 'topup'} onClose={close} busy={topUp.isPending} onSubmit={(amount) => { topUp.mutate({ id, amount }); }} />
      <MoveWalletModal opened={dialog === 'move'} onClose={close} busy={move.isPending} targets={moveTargets(tree.data ?? [], object.ancestors, walletDomainId)} onSubmit={(targetDomainId) => { move.mutate({ id, targetDomainId }); }} />
      <SubscriptionModal opened={dialog === 'subscription'} onClose={close} busy={addSubscription.isPending} onSubmit={(productName, quantity) => { addSubscription.mutate({ walletId: id, productName, quantity }); }} />
      <QuantityModal opened={dialog === 'quantity'} onClose={close} busy={setQuantity.isPending} current={'quantity' in object ? object.quantity : 1} onSubmit={(quantity) => { setQuantity.mutate({ id, quantity }); }} />
      <ConfirmModal opened={dialog === 'delete'} onClose={close} busy={remove.isPending} title={`Delete ${typeLabel(type).toLowerCase()}`} message={`Delete ${name}? This cannot be undone.`} confirmLabel="Delete" onConfirm={() => { remove.mutate({ type, id }); }} />
    </>
  );
}
