import { useSearchParams } from 'react-router-dom';

import { useAccessObject } from '../../api/queries';
import { NotFoundPanel, PageTitle, QueryBoundary } from '../../components/common';
import { typeLabel } from '../../lib/format';
import { Crumbs } from '../resources/ObjectDetail';

import { AccessTab } from './AccessTab';

/**
 * Route `/access?type=&id=`: the Access tab for any resource type, including another team's type this client has no page
 * for. Everything it shows comes from the access endpoints, which read the engine; the object's name is its id where this
 * application has no record of it. The API answers 404 or 403 unless the caller holds `manage_access` on the object.
 */
export function AccessPage() {
  const [params] = useSearchParams();
  const type = params.get('type');
  const id = params.get('id');
  if (!type || !id) return <NotFoundPanel />;
  return <AccessObject key={`${type}:${id}`} type={type} id={id} />;
}

function AccessObject({ type, id }: { type: string; id: string }) {
  const query = useAccessObject(type, id);
  return (
    <QueryBoundary query={query} notFoundWhat={`This ${typeLabel(type)}`}>
      {(object) => (
        <div data-testid="access-page">
          <Crumbs ancestors={object.ancestors} />
          <PageTitle title={object.name} badge={typeLabel(type)} />
          <AccessTab type={type} id={id} permissions={object.permissions} ancestors={object.ancestors} />
        </div>
      )}
    </QueryBoundary>
  );
}
