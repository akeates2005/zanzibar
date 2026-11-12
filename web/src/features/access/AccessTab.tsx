import { Anchor, Button, Card, Group, NativeSelect, SegmentedControl, Stack, Table, Text, Title } from '@mantine/core';
import { useState, type ReactNode } from 'react';
import { Link } from 'react-router-dom';

import { grantAccess, revokeAccess, useCommand } from '../../api/commands';
import { useEffectiveAccess, useGrantAudit, useGrantableGroups, useGrants, useTree } from '../../api/queries';
import type { BreadcrumbDto, DirectoryUser, GrantTargetType, SubjectView } from '../../api/types';
import { EmptyState, QueryBoundary } from '../../components/common';
import { UserPicker } from '../../components/pickers';
import { describeSubject, formatDateTime, humanise, typeLabel } from '../../lib/format';

interface Props {
  type: GrantTargetType;
  id: string;
  /** The object's permission map; its keys are the permissions the "Who can" panel may ask about. */
  permissions: Readonly<Record<string, boolean>>;
  ancestors: readonly BreadcrumbDto[];
}

const subjectChoice = (s: SubjectView) => ({ type: s.type === 'group' ? ('group' as const) : ('user' as const), id: s.id });

/**
 * Who has access to this object and how: direct grants (editable), inherited access (read-only, linked to where it comes
 * from), a form to add a grant, "who can" for any permission, and the audit of changes. Shown only to those who hold
 * manage_access on the object.
 */
export function AccessTab({ type, id, permissions, ancestors }: Props) {
  return (
    <Stack gap="lg" data-testid="access-tab">
      <DirectGrants type={type} id={id} />
      <InheritedGrants type={type} id={id} ancestors={ancestors} />
      <AddGrant type={type} id={id} />
      <WhoCan type={type} id={id} permissions={permissions} />
      <Audit type={type} id={id} />
    </Stack>
  );
}

function Section({ title, children, testId }: { title: string; children: ReactNode; testId: string }) {
  return (
    <Card withBorder padding="md" data-testid={testId}>
      <Title order={5} mb="sm">
        {title}
      </Title>
      {children}
    </Card>
  );
}

function DirectGrants({ type, id }: { type: GrantTargetType; id: string }) {
  const grants = useGrants(type, id);
  const revoke = useCommand(revokeAccess, { success: 'Access removed.' });
  return (
    <Section title="Direct grants" testId="direct-grants">
      <QueryBoundary query={grants}>
        {(view) =>
          view.direct.length === 0 ? (
            <EmptyState title="Nobody has been granted a role on this object directly" />
          ) : (
            <Table.ScrollContainer minWidth={640}>
              <Table verticalSpacing="xs">
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>Subject</Table.Th>
                    <Table.Th>Subject type</Table.Th>
                    <Table.Th>Role</Table.Th>
                    <Table.Th>Granted by</Table.Th>
                    <Table.Th>Date</Table.Th>
                    <Table.Th />
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {view.direct.map((g) => (
                    <Table.Tr key={`${g.relation}|${g.subject.type}|${g.subject.id}`} data-testid={`grant-${g.subject.id}-${g.relation}`}>
                      <Table.Td>{describeSubject(g.subject)}</Table.Td>
                      <Table.Td>{typeLabel(g.subject.type)}</Table.Td>
                      <Table.Td>{humanise(g.relation)}</Table.Td>
                      <Table.Td>{g.grantedBy ?? ''}</Table.Td>
                      <Table.Td>{formatDateTime(g.grantedAt)}</Table.Td>
                      <Table.Td>
                        <Button
                          size="compact-sm"
                          color="red"
                          variant="light"
                          aria-label={`Remove ${g.subject.displayName} ${humanise(g.relation)}`}
                          loading={revoke.isPending}
                          onClick={() => { revoke.mutate({ type, id, relation: g.relation, subject: subjectChoice(g.subject) }); }}
                        >
                          Remove
                        </Button>
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </Table.ScrollContainer>
          )
        }
      </QueryBoundary>
    </Section>
  );
}

function InheritedGrants({ type, id, ancestors }: { type: GrantTargetType; id: string; ancestors: readonly BreadcrumbDto[] }) {
  const grants = useGrants(type, id);
  const tree = useTree();
  // A source is linked only if the user can open it: it appears in their tree or among this object's viewable ancestors.
  const openable = new Set<string>([
    ...(tree.data ?? []).map((n) => `${n.type}:${n.id}`),
    ...ancestors.filter((a) => a.viewable && a.id !== null).map((a) => `${a.type}:${a.id ?? ''}`),
  ]);
  return (
    <Section title="Inherited access" testId="inherited-grants">
      <Text size="sm" c="dimmed" mb="xs">
        Read-only. Administrators of an ancestor are administrators here, and the platform&apos;s support subjects can view everything.
      </Text>
      <QueryBoundary query={grants}>
        {(view) =>
          view.inherited.length === 0 ? (
            <EmptyState title="Nothing is inherited from above" />
          ) : (
            <Table.ScrollContainer minWidth={640}>
              <Table verticalSpacing="xs">
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>Subject</Table.Th>
                    <Table.Th>Role</Table.Th>
                    <Table.Th>Gives here</Table.Th>
                    <Table.Th>Comes from</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {view.inherited.map((g) => (
                    <Table.Tr key={`${g.sourceType}|${g.sourceId}|${g.relation}|${g.subject.type}|${g.subject.id}`}>
                      <Table.Td>{describeSubject(g.subject)}</Table.Td>
                      <Table.Td>{humanise(g.relation)}</Table.Td>
                      <Table.Td>{g.effect}</Table.Td>
                      <Table.Td>
                        {g.sourceType !== 'platform' && openable.has(`${g.sourceType}:${g.sourceId}`) ? (
                          <Anchor component={Link} to={`/resources/${g.sourceType}/${g.sourceId}`}>
                            {typeLabel(g.sourceType)}: {g.sourceName}
                          </Anchor>
                        ) : (
                          <Text span>
                            {typeLabel(g.sourceType)}: {g.sourceName}
                          </Text>
                        )}
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </Table.ScrollContainer>
          )
        }
      </QueryBoundary>
    </Section>
  );
}

function AddGrant({ type, id }: { type: GrantTargetType; id: string }) {
  const grants = useGrants(type, id);
  const groups = useGrantableGroups(type, id);
  const [kind, setKind] = useState<'user' | 'group'>('user');
  const [user, setUser] = useState<DirectoryUser | null>(null);
  const [groupId, setGroupId] = useState('');
  const [relation, setRelation] = useState('');
  const grant = useCommand(grantAccess, { success: 'Access granted.', onSuccess: () => { setUser(null); } });

  const relations = grants.data?.grantableRelations ?? [];
  const chosenRelation = relation !== '' ? relation : (relations[0] ?? '');
  const groupOptions = groups.data ?? [];
  const chosenGroup = groupId !== '' ? groupId : (groupOptions[0]?.id ?? '');
  const subjectId = kind === 'user' ? user?.id : chosenGroup;

  return (
    <Section title="Add grant" testId="add-grant">
      <form
        onSubmit={(event) => {
          event.preventDefault();
          if (subjectId && chosenRelation) grant.mutate({ type, id, relation: chosenRelation, subject: { type: kind, id: subjectId } });
        }}
      >
        <Stack gap="sm" maw={460}>
          <SegmentedControl
            aria-label="Grant to"
            value={kind}
            onChange={(v) => { setKind(v === 'group' ? 'group' : 'user'); }}
            data={[{ value: 'user', label: 'A user' }, { value: 'group', label: 'A group' }]}
          />
          {kind === 'user' ? (
            <UserPicker label="User" value={user} onChange={setUser} />
          ) : (
            <NativeSelect
              label="Group"
              description="Only groups the rules allow here: this customer's groups, and platform groups for platform administrators."
              data={groupOptions.length === 0 ? [{ value: '', label: 'No group can be granted here' }] : groupOptions.map((g) => ({ value: g.id, label: g.ownerType === 'Platform' ? `${g.name} (platform)` : g.name }))}
              value={chosenGroup}
              onChange={(e) => { setGroupId(e.currentTarget.value); }}
              disabled={groupOptions.length === 0}
            />
          )}
          <NativeSelect
            label="Role"
            data={relations.map((r) => ({ value: r, label: humanise(r) }))}
            value={chosenRelation}
            onChange={(e) => { setRelation(e.currentTarget.value); }}
          />
          <Group>
            <Button type="submit" loading={grant.isPending} disabled={!subjectId || !chosenRelation}>
              Grant access
            </Button>
          </Group>
        </Stack>
      </form>
    </Section>
  );
}

function WhoCan({ type, id, permissions }: { type: GrantTargetType; id: string; permissions: Readonly<Record<string, boolean>> }) {
  const names = Object.keys(permissions).sort();
  const [permission, setPermission] = useState<string | null>(null);
  const effective = useEffectiveAccess(type, id, permission);
  return (
    <Section title="Who can" testId="who-can">
      <Stack gap="sm" maw={460}>
        <NativeSelect
          label="Permission"
          data={[{ value: '', label: 'Choose a permission' }, ...names.map((n) => ({ value: n, label: humanise(n) }))]}
          value={permission ?? ''}
          onChange={(e) => { setPermission(e.currentTarget.value || null); }}
        />
        {permission === null ? null : (
          <QueryBoundary query={effective}>
            {(holders) =>
              holders.length === 0 ? (
                <Text c="dimmed">Nobody holds this permission here.</Text>
              ) : (
                <ul aria-label="Users who hold this permission" style={{ margin: 0 }}>
                  {holders.map((h) => (
                    <li key={h.id}>{describeSubject(h)}</li>
                  ))}
                </ul>
              )
            }
          </QueryBoundary>
        )}
      </Stack>
    </Section>
  );
}

function Audit({ type, id }: { type: GrantTargetType; id: string }) {
  const audit = useGrantAudit(type, id);
  return (
    <Section title="Audit" testId="audit">
      <QueryBoundary query={audit}>
        {(entries) =>
          entries.length === 0 ? (
            <EmptyState title="No grant has been changed here yet" />
          ) : (
            <Table.ScrollContainer minWidth={640}>
              <Table verticalSpacing="xs">
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>When</Table.Th>
                    <Table.Th>Action</Table.Th>
                    <Table.Th>Role</Table.Th>
                    <Table.Th>Subject</Table.Th>
                    <Table.Th>By</Table.Th>
                    <Table.Th>Status</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {entries.map((e) => (
                    <Table.Tr key={e.id}>
                      <Table.Td>{formatDateTime(e.at)}</Table.Td>
                      <Table.Td>{e.action}</Table.Td>
                      <Table.Td>{humanise(e.relation)}</Table.Td>
                      <Table.Td>{e.subjectType === 'group' ? `group ${e.subjectId}` : e.subjectId}</Table.Td>
                      <Table.Td>{e.actor}</Table.Td>
                      <Table.Td>{e.status}</Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            </Table.ScrollContainer>
          )
        }
      </QueryBoundary>
    </Section>
  );
}
