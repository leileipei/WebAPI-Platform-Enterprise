export function organizationPermissions(organizationId,permissions){
  return {permissions,rolePermissions:[{organizationId,permissions}]};
}
