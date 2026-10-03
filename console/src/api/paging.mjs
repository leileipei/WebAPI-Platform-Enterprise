export function withPage(path,page){const url=new URL(path,'http://console.local');url.searchParams.set('page',String(page));return url.pathname+url.search;}
export async function readAllPages(request,path,options={}){
 let page=1,result,items=[];
 do{result=await request(withPage(path,page),options);items.push(...result.items);if(page*result.pageSize>=result.total||result.items.length<result.pageSize)break;page++;}while(true);
 return {...result,items,page:1,pageSize:items.length};
}
