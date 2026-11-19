import { Anchor, Badge, Group, Paper, Stack, Text } from '@mantine/core';
import { Link } from 'react-router-dom';
import { useState } from 'react';

import { createCustomer, useCommand } from '../../api/commands';
import { useMe, useTree } from '../../api/queries';
import { ActionButton, EmptyState, PageTitle, QueryBoundary } from '../../components/common';
import { buildTree, countOpenable, type TreeItem } from '../../lib/tree';
import { typeLabel } from '../../lib/format';
import { TypeIcon, typeVisual } from '../../components/typeVisuals';
import { TextPromptModal } from './dialogs';
import classes from './ResourceBrowser.module.css';

function Row({ item }: { item: TreeItem }) {
  const { color } = typeVisual(item.type);
  return (
    <li className={classes.item}>
      <Group gap="sm" wrap="nowrap" className={classes.row} data-testid={`tree-${item.type}-${item.id ?? 'label'}`}>
        <TypeIcon type={item.type} />
        {item.id === null ? (
          // An ancestor the user cannot view appears as plain text, never as a link (PRD: Resource browser).
          <Text span c="dimmed" fw={500} title="You cannot open this">
            {item.name}
          </Text>
        ) : (
          <Anchor component={Link} to={`/resources/${item.type}/${item.id}`} className={classes.link}>
            {item.name}
          </Anchor>
        )}
        <Badge size="xs" variant="light" color={color} className={classes.badge}>
          {typeLabel(item.type)}
        </Badge>
      </Group>
      {item.children.length > 0 ? (
        <ul className={classes.children}>
          {item.children.map((child) => (
            <Row key={child.key} item={child} />
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
            <Paper withBorder p="sm">
              <ul className={classes.root} aria-label="Resources">
                {items.map((item) => (
                  <Row key={item.key} item={item} />
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
