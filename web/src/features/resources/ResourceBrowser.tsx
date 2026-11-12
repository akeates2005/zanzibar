import { Anchor, Badge, Group, Paper, Stack, Text } from '@mantine/core';
import { Link } from 'react-router-dom';
import { useState } from 'react';

import { createCustomer, useCommand } from '../../api/commands';
import { useMe, useTree } from '../../api/queries';
import { ActionButton, EmptyState, PageTitle, QueryBoundary } from '../../components/common';
import { buildTree, countOpenable, type TreeItem } from '../../lib/tree';
import { typeLabel } from '../../lib/format';
import { TextPromptModal } from './dialogs';

function Row({ item, depth }: { item: TreeItem; depth: number }) {
  return (
    <li style={{ listStyle: 'none' }}>
      <Group gap="xs" ml={depth * 20} my={2} wrap="nowrap" data-testid={`tree-${item.type}-${item.id ?? 'label'}`}>
        <Badge size="xs" variant="outline" color="gray">
          {typeLabel(item.type)}
        </Badge>
        {item.id === null ? (
          // An ancestor the user cannot view appears as plain text, never as a link (PRD: Resource browser).
          <Text span c="dimmed" title="You cannot open this">
            {item.name}
          </Text>
        ) : (
          <Anchor component={Link} to={`/resources/${item.type}/${item.id}`}>
            {item.name}
          </Anchor>
        )}
      </Group>
      {item.children.length > 0 ? (
        <ul style={{ margin: 0, padding: 0 }}>
          {item.children.map((child) => (
            <Row key={child.key} item={child} depth={depth + 1} />
          ))}
        </ul>
      ) : null}
    </li>
  );
}

/** Browse the tree of every object the signed-in user can view. */
export function ResourceBrowser() {
  const tree = useTree();
  const me = useMe();
  const [creating, setCreating] = useState(false);
  const create = useCommand(createCustomer, { success: (_, v) => `Customer ${v.name} created.`, onSuccess: () => { setCreating(false); } });

  return (
    <Stack>
      <PageTitle
        title="Resources"
        actions={
          <ActionButton label="New customer" permission="create_customer" permissions={me.data?.platformPermissions ?? {}} onClick={() => { setCreating(true); }} />
        }
      />
      <QueryBoundary query={tree}>
        {(nodes) => {
          const items = buildTree(nodes);
          if (countOpenable(items) === 0) {
            return (
              <EmptyState title="You do not have access to anything yet">
                Ask an administrator to grant you a role on a customer, domain, wallet or subscription, then come back.
              </EmptyState>
            );
          }
          return (
            <Paper withBorder p="md">
              <ul style={{ margin: 0, padding: 0 }} aria-label="Resources">
                {items.map((item) => (
                  <Row key={item.key} item={item} depth={0} />
                ))}
              </ul>
            </Paper>
          );
        }}
      </QueryBoundary>
      <TextPromptModal
        opened={creating}
        onClose={() => { setCreating(false); }}
        busy={create.isPending}
        title="New customer"
        label="Customer name"
        submitLabel="Create customer"
        onSubmit={(name) => { create.mutate({ name }); }}
      />
    </Stack>
  );
}
