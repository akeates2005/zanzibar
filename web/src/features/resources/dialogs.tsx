import { Button, Group, Modal, NumberInput, Select, Stack, Text, TextInput } from '@mantine/core';
import { useState, type ReactNode, type SyntheticEvent } from 'react';

import { toNumber } from '../../lib/format';

interface BaseProps {
  opened: boolean;
  onClose: () => void;
  busy?: boolean;
}

function Form({ onSubmit, children, submitLabel, busy, onClose, canSubmit }: {
  onSubmit: () => void;
  children: ReactNode;
  submitLabel: string;
  busy: boolean;
  onClose: () => void;
  canSubmit: boolean;
}) {
  return (
    <form
      onSubmit={(event: SyntheticEvent) => {
        event.preventDefault();
        if (canSubmit) onSubmit();
      }}
    >
      <Stack>
        {children}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" loading={busy} disabled={!canSubmit}>
            {submitLabel}
          </Button>
        </Group>
      </Stack>
    </form>
  );
}

/** Asks for one line of text (a name). Re-mounts its state each time it opens, so it never shows a stale value. */
export function TextPromptModal({ opened, onClose, busy = false, title, label, initial = '', submitLabel, onSubmit }: BaseProps & {
  title: string;
  label: string;
  initial?: string;
  submitLabel: string;
  onSubmit: (value: string) => void;
}) {
  return (
    <Modal opened={opened} onClose={onClose} title={title}>
      {opened ? <TextPromptBody {...{ onClose, busy, label, initial, submitLabel, onSubmit }} /> : null}
    </Modal>
  );
}

function TextPromptBody({ onClose, busy, label, initial, submitLabel, onSubmit }: Omit<Parameters<typeof TextPromptModal>[0], 'opened' | 'title'> & { busy: boolean; initial: string }) {
  const [value, setValue] = useState(initial);
  return (
    <Form onSubmit={() => { onSubmit(value.trim()); }} submitLabel={submitLabel} busy={busy} onClose={onClose} canSubmit={value.trim().length > 0}>
      <TextInput label={label} value={value} onChange={(e) => { setValue(e.currentTarget.value); }} data-autofocus />
    </Form>
  );
}

export function AmountModal({ opened, onClose, busy = false, onSubmit }: BaseProps & { onSubmit: (amount: number) => void }) {
  return (
    <Modal opened={opened} onClose={onClose} title="Top up wallet">
      {opened ? <AmountBody onClose={onClose} busy={busy} onSubmit={onSubmit} /> : null}
    </Modal>
  );
}

function AmountBody({ onClose, busy, onSubmit }: { onClose: () => void; busy: boolean; onSubmit: (amount: number) => void }) {
  const [amount, setAmount] = useState<number | string>('');
  const value = toNumber(amount);
  return (
    <Form onSubmit={() => { onSubmit(value); }} submitLabel="Top up" busy={busy} onClose={onClose} canSubmit={value > 0}>
      <NumberInput label="Amount" value={amount} onChange={setAmount} min={0} decimalScale={2} data-autofocus />
    </Form>
  );
}

export function MoveWalletModal({ opened, onClose, busy = false, targets, onSubmit }: BaseProps & {
  targets: { id: string; name: string }[];
  onSubmit: (domainId: string) => void;
}) {
  return (
    <Modal opened={opened} onClose={onClose} title="Move wallet to another domain">
      {opened ? <MoveBody onClose={onClose} busy={busy} targets={targets} onSubmit={onSubmit} /> : null}
    </Modal>
  );
}

function MoveBody({ onClose, busy, targets, onSubmit }: { onClose: () => void; busy: boolean; targets: { id: string; name: string }[]; onSubmit: (domainId: string) => void }) {
  const [target, setTarget] = useState<string | null>(null);
  return (
    <Form onSubmit={() => { if (target) onSubmit(target); }} submitLabel="Move" busy={busy} onClose={onClose} canSubmit={target !== null}>
      {targets.length === 0 ? <Text c="dimmed">There is no other domain under this customer that you can see.</Text> : null}
      <Select label="Target domain" data={targets.map((t) => ({ value: t.id, label: t.name }))} value={target} onChange={setTarget} allowDeselect={false} />
    </Form>
  );
}

export function SubscriptionModal({ opened, onClose, busy = false, onSubmit }: BaseProps & { onSubmit: (productName: string, quantity: number) => void }) {
  return (
    <Modal opened={opened} onClose={onClose} title="Add subscription">
      {opened ? <SubscriptionBody onClose={onClose} busy={busy} onSubmit={onSubmit} /> : null}
    </Modal>
  );
}

function SubscriptionBody({ onClose, busy, onSubmit }: { onClose: () => void; busy: boolean; onSubmit: (productName: string, quantity: number) => void }) {
  const [product, setProduct] = useState('');
  const [quantity, setQuantity] = useState<number | string>(1);
  const amount = toNumber(quantity);
  return (
    <Form onSubmit={() => { onSubmit(product.trim(), amount); }} submitLabel="Add subscription" busy={busy} onClose={onClose} canSubmit={product.trim().length > 0 && amount >= 1}>
      <TextInput label="Product name" value={product} onChange={(e) => { setProduct(e.currentTarget.value); }} data-autofocus />
      <NumberInput label="Quantity" value={quantity} onChange={setQuantity} min={1} allowDecimal={false} />
    </Form>
  );
}

export function QuantityModal({ opened, onClose, busy = false, current, onSubmit }: BaseProps & { current: number; onSubmit: (quantity: number) => void }) {
  return (
    <Modal opened={opened} onClose={onClose} title="Change quantity">
      {opened ? <QuantityBody onClose={onClose} busy={busy} current={current} onSubmit={onSubmit} /> : null}
    </Modal>
  );
}

function QuantityBody({ onClose, busy, current, onSubmit }: { onClose: () => void; busy: boolean; current: number; onSubmit: (quantity: number) => void }) {
  const [quantity, setQuantity] = useState<number | string>(current);
  const amount = toNumber(quantity);
  return (
    <Form onSubmit={() => { onSubmit(amount); }} submitLabel="Change quantity" busy={busy} onClose={onClose} canSubmit={amount >= 1}>
      <NumberInput label="Quantity" value={quantity} onChange={setQuantity} min={1} allowDecimal={false} data-autofocus />
    </Form>
  );
}

export function ConfirmModal({ opened, onClose, busy = false, title, message, confirmLabel, onConfirm }: BaseProps & {
  title: string;
  message: string;
  confirmLabel: string;
  onConfirm: () => void;
}) {
  return (
    <Modal opened={opened} onClose={onClose} title={title}>
      <Stack>
        <Text>{message}</Text>
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose}>
            Cancel
          </Button>
          <Button color="red" loading={busy} onClick={onConfirm}>
            {confirmLabel}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
