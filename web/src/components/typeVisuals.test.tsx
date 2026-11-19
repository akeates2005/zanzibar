import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

import { renderWithProviders } from '../test/render';
import { TypeIcon, typeVisual } from './typeVisuals';

describe('typeVisual', () => {
  it('gives a known type its own colour', () => {
    expect(typeVisual('commerce/wallet').color).toBe('teal');
  });

  it("gives another team's type, unknown to the client, a neutral look instead of failing", () => {
    expect(typeVisual('billing/invoice').color).toBe('gray');
    renderWithProviders(
      <span data-testid="icon">
        <TypeIcon type="billing/invoice" />
      </span>,
    );
    expect(screen.getByTestId('icon').querySelector('svg')).not.toBeNull();
  });
});
