export function tabFocusTarget(items,active,backwards,fallback){
 if(!items.length)return fallback;
 const index=items.indexOf(active);
 if(index<0)return backwards?items.at(-1):items[0];
 if(backwards&&index===0)return items.at(-1);
 if(!backwards&&index===items.length-1)return items[0];
 return null;
}
export function recoveryFocusTarget(items,active,fallback){
 return items.includes(active)?null:items[0]||fallback;
}
