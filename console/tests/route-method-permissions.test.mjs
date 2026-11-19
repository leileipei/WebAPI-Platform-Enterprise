import test from 'node:test';
import assert from 'node:assert/strict';
import {readRouteMethodOptions, routeMethodSelection, toggleRouteMethod} from '../src/applications/route-method-permissions.mjs';

const route = {routeId:'route-a', path:'/orders', authorizationRevision:7, methods:['GET','POST']};
const second = {routeId:'route-b', path:'/orders/{id}', authorizationRevision:12, methods:['GET','DELETE']};
const options = {environmentId:'target', apiId:'orders', routes:[route,second]};

test('route method options qualify only for their exact target scope and valid unique route revisions', () => {
  assert.equal(readRouteMethodOptions(options,'target','orders'),options);
  assert.equal(readRouteMethodOptions(options,'another-environment','orders'),null);
  assert.equal(readRouteMethodOptions(options,'target','another-api'),null);
  for (const routes of [[route,route],[{...route,authorizationRevision:0}],[{...route,methods:['GET','GET']}],
    [{...route,methods:['get']}],[{...route,methods:['CONNECT']}]]) {
    assert.equal(readRouteMethodOptions({...options,routes},'target','orders'),null);
  }
});

test('only explicitly selected methods enter the grant and the current route revision is attached', () => {
  assert.deepEqual(routeMethodSelection([],options),[]);
  const once = toggleRouteMethod([],route,'POST',true);
  assert.deepEqual(once,[{routeId:route.routeId,authorizationRevision:7,methods:['POST']}]);
  const twice = toggleRouteMethod(once,route,'GET',true);
  assert.deepEqual(twice,[{routeId:route.routeId,authorizationRevision:7,methods:['GET','POST']}]);
  assert.deepEqual(toggleRouteMethod(twice,route,'POST',false),[{routeId:route.routeId,authorizationRevision:7,methods:['GET']}]);
  assert.deepEqual(toggleRouteMethod(once,route,'POST',false),[]);
  assert.deepEqual(toggleRouteMethod(once,route,'DELETE',true),once);
});

test('refresh drops stale or unverifiable selections without adding choices from the new revision', () => {
  const stale = [
    {routeId:route.routeId,authorizationRevision:6,methods:['GET']},
    {routeId:second.routeId,authorizationRevision:12,methods:['GET']}
  ];
  assert.deepEqual(routeMethodSelection(stale,options),[stale[1]]);
  assert.deepEqual(routeMethodSelection(stale,null),[]);
  assert.deepEqual(routeMethodSelection([{...stale[1],methods:['GET','POST']}],options),[]);
  assert.deepEqual(routeMethodSelection([stale[1],stale[1]],options),[]);
});
