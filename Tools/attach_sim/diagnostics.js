// Independent geometry diagnostics only; never queried by the runtime/simulator.
const sub=(a,b)=>a.map((v,i)=>v-b[i]);
const dot=(a,b)=>a.reduce((s,v,i)=>s+v*b[i],0);
const cross=(a,b)=>[a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]];
export function bounds(tris){const b=[Infinity,Infinity,Infinity,-Infinity,-Infinity,-Infinity];for(const t of tris)for(const p of t)for(let i=0;i<3;i++){b[i]=Math.min(b[i],p[i]);b[i+3]=Math.max(b[i+3],p[i]);}return b;}
function boxesMeet(a,b){return [0,1,2].every(i=>a[i]<=b[i+3]+1e-8&&b[i]<=a[i+3]+1e-8);}
export function triangleMeet(a,b){
  const ea=[sub(a[1],a[0]),sub(a[2],a[1]),sub(a[0],a[2])];
  const eb=[sub(b[1],b[0]),sub(b[2],b[1]),sub(b[0],b[2])];
  const na=cross(ea[0],ea[1]),nb=cross(eb[0],eb[1]);
  if(dot(na,na)<1e-24||dot(nb,nb)<1e-24)return false;
  const axes=[na,nb,...ea.flatMap(x=>eb.map(y=>cross(x,y)))];
  // In-plane axes also decide coplanar triangles (including zero-volume sheets).
  axes.push(...ea.map(e=>cross(na,e)),...eb.map(e=>cross(nb,e)));
  for(const axis of axes){const length=Math.sqrt(dot(axis,axis));if(length<1e-12)continue;
    const ap=a.map(p=>dot(p,axis)/length),bp=b.map(p=>dot(p,axis)/length);
    if(Math.max(...ap)<Math.min(...bp)-1e-8||Math.max(...bp)<Math.min(...ap)-1e-8)return false;
  }return true;
}
export function tree(tris){
  const box=bounds(tris);if(tris.length<=12)return {box,tris};
  const ext=[0,1,2].map(i=>box[i+3]-box[i]);const axis=ext.indexOf(Math.max(...ext));
  tris.sort((a,b)=>a.reduce((s,p)=>s+p[axis],0)-b.reduce((s,p)=>s+p[axis],0));
  const mid=Math.floor(tris.length/2);return {box,left:tree(tris.slice(0,mid)),right:tree(tris.slice(mid))};
}
export function treesMeet(a,b){
  if(!boxesMeet(a.box,b.box))return false;
  if(a.tris&&b.tris)return a.tris.some(x=>b.tris.some(y=>triangleMeet(x,y)));
  if(a.tris)return treesMeet(a,b.left)||treesMeet(a,b.right);
  return treesMeet(a.left,b)||treesMeet(a.right,b);
}
// Hypothetical-control helpers: a rigid prop translated by `off` against a static tree.
export const shift=(tri,off)=>tri.map(p=>[p[0]+off[0],p[1]+off[1],p[2]+off[2]]);
export function treesMeetShifted(a,off,b){
  const box=[a.box[0]+off[0],a.box[1]+off[1],a.box[2]+off[2],a.box[3]+off[0],a.box[4]+off[1],a.box[5]+off[2]];
  if(!boxesMeet(box,b.box))return false;
  if(a.tris&&b.tris)return a.tris.some(x=>{const sx=shift(x,off);return b.tris.some(y=>triangleMeet(sx,y));});
  if(a.tris)return treesMeetShifted(a,off,b.left)||treesMeetShifted(a,off,b.right);
  return treesMeetShifted(a.left,off,b)||treesMeetShifted(a.right,off,b);
}
export function worldTriangles(group,THREE){
  group.updateMatrixWorld(true);const tris=[];
  group.traverse(mesh=>{if(!mesh.isMesh)return;const geo=mesh.geometry,pos=geo.getAttribute('position'),ind=geo.index;
    const count=ind?ind.count:pos.count;
    for(let i=0;i+2<count;i+=3){const v=[];for(let k=0;k<3;k++)v.push(new THREE.Vector3().fromBufferAttribute(pos,ind?ind.getX(i+k):i+k).applyMatrix4(mesh.matrixWorld).toArray());
      tris.push(v);}});
  return tris;
}
// Largest travel distance (<= far, >= floor) at which the prop meets any static tree, stepping toward the origin.
export function firstMeshContact(moving,direction,statics,far,floor,step=.001){
  const at=t=>statics.some(s=>treesMeetShifted(moving,direction.map(x=>x*t),s));
  let previous=far,current=far;
  if(at(far))return {distance:far,hit:true,startsInContact:true};
  while(current>floor){current=Math.max(floor,current-step);
    if(at(current)){let lo=current,hi=previous;
      for(let i=0;i<24;i++){const mid=(lo+hi)/2;if(at(mid))lo=mid;else hi=mid;}
      return {distance:lo,hit:true,startsInContact:false};}
    previous=current;}
  return {distance:floor,hit:false,startsInContact:false};
}
export function meshDiagnostics(groups,THREE){
  const rows=[];const origin=new THREE.Vector3();
  for(const group of groups){
    group.updateMatrixWorld(true);const tris=[];let nearest=Infinity;
    group.traverse(mesh=>{if(!mesh.isMesh)return;const geo=mesh.geometry, pos=geo.getAttribute('position'),ind=geo.index;
      const count=ind?ind.count:pos.count;
      for(let i=0;i+2<count;i+=3){const v=[];for(let k=0;k<3;k++)v.push(new THREE.Vector3().fromBufferAttribute(pos,ind?ind.getX(i+k):i+k).applyMatrix4(mesh.matrixWorld));
        const tri=new THREE.Triangle(...v);const point=new THREE.Vector3();tri.closestPointToPoint(origin,point);
        const distance=point.length();
        if(Number.isFinite(distance))nearest=Math.min(nearest,distance);
        else for(let e=0;e<3;e++){
          // Original windchime assets contain degenerate triangles; retain their edges.
          new THREE.Line3(v[e],v[(e+1)%3]).closestPointToPoint(origin,true,point);
          if(Number.isFinite(point.length()))nearest=Math.min(nearest,point.length());
        }
        tris.push(v.map(p=>p.toArray()));
      }
    });
    rows.push({id:group.userData.id,tree:tree(tris),triangleCount:tris.length,nearest,
      contactRadiusM:group.userData.data.contactCollisionRadiusM});
  }
  const pairs=[];let candidates=0;
  for(let i=0;i<rows.length;i++)for(let j=i+1;j<rows.length;j++){
    if(!boxesMeet(rows[i].tree.box,rows[j].tree.box))continue;candidates++;
    if(treesMeet(rows[i].tree,rows[j].tree))pairs.push([rows[i].id,rows[j].id]);
  }
  return {testedPairs:rows.length*(rows.length-1)/2,aabbCandidatePairs:candidates,
    surfaceIntersectPairs:pairs.length,pairs,
    metric:'triangle-surface intersection, geometry only (ignores texture alpha); fully contained meshes not counted',
    items:rows.map(r=>({itemId:r.id,triangleCount:r.triangleCount,meshDistanceM:r.nearest,
      meshContactGapMm:Number.isFinite(r.contactRadiusM)?1000*(r.nearest-r.contactRadiusM):null}))};
}
export const diagnosticSelfTest=()=>({disjoint:triangleMeet([[0,0,0],[1,0,0],[0,1,0]],[[2,0,0],[3,0,0],[2,1,0]]),
  crossing:triangleMeet([[0,0,0],[1,0,0],[0,1,0]],[[.25,.25,-1],[.25,.25,1],[.75,.25,0]])});
export const treeSelfTest=()=>{
  const sheet=(x)=>[[[x,0,0],[x,1,0],[x,0,1]],[[x,1,0],[x,1,1],[x,0,1]]];
  const wall=tree(sheet(3)),flying=tree(sheet(0));
  const first=firstMeshContact(flying,[1,0,0],[wall],10,0);
  return {distance:first.distance,hit:first.hit};
};
