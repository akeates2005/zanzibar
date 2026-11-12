import { Select } from '@mantine/core';
import { useState } from 'react';

import { useUserSearch } from '../api/queries';
import type { DirectoryUser } from '../api/types';

/**
 * Picks one user from the directory. The search is done by the server (it also matches email), so client-side filtering is
 * switched off; the chosen user is kept so its label survives the next search replacing the options.
 */
export function UserPicker({ label, value, onChange, disabled = false }: {
  label: string;
  value: DirectoryUser | null;
  onChange: (user: DirectoryUser | null) => void;
  disabled?: boolean;
}) {
  const [search, setSearch] = useState('');
  const results = useUserSearch(search);
  const options = new Map<string, DirectoryUser>();
  if (value) options.set(value.id, value);
  for (const user of results.data ?? []) options.set(user.id, user);

  return (
    <Select
      label={label}
      placeholder="Search by name, email or id"
      searchable
      clearable
      disabled={disabled}
      nothingFoundMessage="No matching user"
      searchValue={search}
      onSearchChange={setSearch}
      filter={({ options: all }) => all}
      data={[...options.values()].map((u) => ({ value: u.id, label: `${u.displayName} (${u.id})` }))}
      value={value?.id ?? null}
      onChange={(id) => { onChange(id ? (options.get(id) ?? null) : null); }}
    />
  );
}
