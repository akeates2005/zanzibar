import { ThemeIcon } from '@mantine/core';
import { IconBuilding, IconCube, IconReceipt, IconUsersGroup, IconWallet, IconWorld, type Icon } from '@tabler/icons-react';

interface TypeVisual {
  icon: Icon;
  color: string;
}

const VISUALS: Record<string, TypeVisual> = {
  platform: { icon: IconWorld, color: 'brand' },
  customer: { icon: IconBuilding, color: 'violet' },
  'commerce/domain': { icon: IconWorld, color: 'cyan' },
  'commerce/wallet': { icon: IconWallet, color: 'teal' },
  'commerce/subscription': { icon: IconReceipt, color: 'orange' },
  group: { icon: IconUsersGroup, color: 'grape' },
};

// Types come from the engine's schema, so another team's type arrives here unknown; it gets a neutral look, never an error.
const FALLBACK: TypeVisual = { icon: IconCube, color: 'gray' };

export const typeVisual = (type: string): TypeVisual => VISUALS[type] ?? FALLBACK;

/** A small tinted icon for a resource type. Decorative: the type's name is always shown next to it as text. */
export function TypeIcon({ type, size = 26 }: { type: string; size?: number }) {
  const { icon: Glyph, color } = typeVisual(type);
  return (
    <ThemeIcon variant="light" color={color} size={size} radius="md" aria-hidden>
      <Glyph size={size * 0.6} stroke={1.75} />
    </ThemeIcon>
  );
}
