import {useCallback, useEffect, useMemo, useState} from 'react';
import {apiRequest} from '../api/client';
import {useSession} from '../auth/SessionProvider';
import {rotationGuid} from './formal-rotation-state.mjs';

export function useFormalRotationEligibility(applicationId: string|null) {
  const {user, error} = useSession();
  const [refresh, setRefresh] = useState(0);
  const [result, setResult] = useState<{request:{path:string}; credentialIds:Set<string>}>();
  const id = rotationGuid(applicationId);
  const authority = user && !user.mustChangePassword && !error ? JSON.stringify([user.id, user.permissions, user.scopes]) : null;
  const request = useMemo(() => id && authority ? {path:`/applications/${id}/formal-rotation-eligibility`} : null,
    [id, authority, refresh]);

  useEffect(() => {
    if (!request) return;
    const controller = new AbortController();
    const load = async () => {
      try {
        const value = await apiRequest<{credentialIds:unknown}>(request.path, {signal:controller.signal});
        if (controller.signal.aborted || !Array.isArray(value?.credentialIds)) return;
        const ids = value.credentialIds.map(rotationGuid);
        if (ids.every(item => item !== null)) setResult({request, credentialIds:new Set(ids)});
      } catch {
        // The lookup is an additional authorization check for formal mode.
        if (!controller.signal.aborted) setResult(undefined);
      }
    };
    void load();
    return () => controller.abort();
  }, [request]);

  const refreshEligibility = useCallback(() => setRefresh(value => value + 1), []);
  return {credentialIds:result?.request === request ? result?.credentialIds : undefined, refreshEligibility};
}
