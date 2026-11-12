import type { components } from './schema';

type Schemas = components['schemas'];

export type CustomerDto = Schemas['CustomerDto'];
export type DomainDto = Schemas['DomainDto'];
export type WalletDto = Schemas['WalletDto'];
export type SubscriptionDto = Schemas['SubscriptionDto'];
export type BreadcrumbDto = Schemas['BreadcrumbDto'];
export type TreeNodeDto = Schemas['TreeNodeDto'];
export type ObjectSummaryDto = Schemas['ObjectSummaryDto'];
export type MeResponse = Schemas['MeResponse'];
export type Problem = Schemas['Problem'];
export type GroupSummary = Schemas['GroupSummary'];
export type GroupDetail = Schemas['GroupDetailDto'];
export type GrantsView = Schemas['GrantsView'];
export type DirectGrantView = Schemas['DirectGrantView'];
export type InheritedGrantView = Schemas['InheritedGrantView'];
export type SubjectView = Schemas['SubjectView'];
export type GrantAuditView = Schemas['GrantAuditView'];
export type ExplainResponse = Schemas['ExplainResponse'];
export type ExplanationStep = Schemas['ExplanationStepDto'];
export type DirectoryUser = Schemas['DirectoryUser'];
export type OutboxDto = Schemas['OutboxDto'];
export type ReconciliationDto = Schemas['ReconciliationDto'];

/** The four business objects that have a detail screen, named as the API names them. */
export type ObjectType = 'customer' | 'domain' | 'wallet' | 'subscription';

export type ObjectDto = CustomerDto | DomainDto | WalletDto | SubscriptionDto;

/** The DTO each object type is returned as. */
interface ObjectByTypeMap {
  customer: CustomerDto;
  domain: DomainDto;
  wallet: WalletDto;
  subscription: SubscriptionDto;
}
export type ObjectByType = ObjectByTypeMap;

/** Object types that can take grants: the business objects plus the platform. */
export type GrantTargetType = ObjectType | 'platform';

export const OBJECT_TYPES: readonly ObjectType[] = ['customer', 'domain', 'wallet', 'subscription'];

export function isObjectType(value: string | undefined): value is ObjectType {
  return value !== undefined && (OBJECT_TYPES as readonly string[]).includes(value);
}

export function isGrantTargetType(value: string | undefined): value is GrantTargetType {
  return value === 'platform' || isObjectType(value);
}
