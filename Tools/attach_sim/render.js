// Rendering adapter only: the editor owns the renderer, model loader and prop placement.
import * as THREE from 'three';
import {EditorView, toThreePosition} from '/editor/js/view.js';
import {meshDiagnostics, diagnosticSelfTest, treeSelfTest, tree, shift, worldTriangles, firstMeshContact} from '/diagnostics.js';
const diagnosticTest=diagnosticSelfTest();
if(diagnosticTest.disjoint||!diagnosticTest.crossing||Math.abs(treeSelfTest().distance-3)>1e-6)throw new Error('triangle diagnostic self-test failed');
const view = new EditorView(document.querySelector('#view'));
cancelAnimationFrame(view._frame);
view.refMode = true; view.v2Mode = true;
view.state = {bounds:[[-1,-1],[1,1]]};
view._buildWorld();
for (const child of [...view.worldGroup.children]) if (!child.isLight && child !== view.worldGroup.children.find(x=>x.isLight)?.target) view.worldGroup.remove(child);
view.scene.fog = null; view.scene.background = new THREE.Color(0x1b2330);
view.gizmoHelper.visible = false; view.regionGroup.visible = false;
view.renderer.shadowMap.enabled = false;
view.renderer.setPixelRatio(1);
view.renderer.setSize(512,512,false);
const coreData = await fetch('/core.json').then(x=>x.json());
let core;
if (coreData.position) {
  const geo = new THREE.BufferGeometry();
  geo.setAttribute('position', new THREE.Float32BufferAttribute(coreData.position,3));
  geo.setAttribute('uv', new THREE.Float32BufferAttribute(coreData.uv,2));
  geo.computeVertexNormals(); geo.computeBoundingBox();
  const center = geo.boundingBox.getCenter(new THREE.Vector3());
  geo.translate(-center.x,-center.y,-center.z);
  const size = geo.boundingBox.getSize(new THREE.Vector3());
  geo.scale(1/Math.max(size.x,size.y,size.z),1/Math.max(size.x,size.y,size.z),1/Math.max(size.x,size.y,size.z));
  const texture = await new THREE.TextureLoader().loadAsync('/core.png');
  texture.colorSpace = THREE.SRGBColorSpace;
  core = new THREE.Mesh(geo,new THREE.MeshStandardMaterial({map:texture,roughness:1,side:THREE.DoubleSide}));
} else core = new THREE.Mesh(new THREE.SphereGeometry(.5,64,32),new THREE.MeshStandardMaterial({color:0xd84343,roughness:1}));
view.scene.add(core);
const coreMaterial = core.material;
const keyMaterial = new THREE.MeshBasicMaterial({color:0xff00ff,side:THREE.DoubleSide,toneMapped:false});
// Preserve texture alpha cutouts and transparent material state in the occluder pass.
const blackCache = new Map();
function occluder(material) {
  if (Array.isArray(material)) return material.map(occluder);
  if (!blackCache.has(material)) blackCache.set(material,new THREE.MeshBasicMaterial({
    color:0,map:material.map,alphaMap:material.alphaMap,alphaTest:material.alphaTest,
    transparent:material.transparent,opacity:material.opacity,side:material.side,
    depthTest:material.depthTest,depthWrite:material.depthWrite,toneMapped:false,
  }));
  return blackCache.get(material);
}
const scratch = document.createElement('canvas'); scratch.width=512; scratch.height=512;
const ctx=scratch.getContext('2d',{willReadFrequently:true});
const props = new THREE.Group(); view.scene.add(props);
const camera=new THREE.OrthographicCamera(-1,1,1,-1,.001,100);
const directions=[[1,0,0],[-1,0,0],[0,1,0],[0,-1,0],[0,0,1],[0,0,-1]];
const labels=['+X','-X','+Y','-Y','+Z','-Z'];
function capture(){view.renderer.render(view.scene,camera);return view.canvas.toDataURL('image/png');}
function keyPixels(){ctx.clearRect(0,0,512,512);ctx.drawImage(view.canvas,0,0,512,512);const a=ctx.getImageData(0,0,512,512).data;let n=0;for(let i=0;i<a.length;i+=4)if(a[i]===255&&a[i+1]===0&&a[i+2]===255)n++;return n;}
window.renderAttachment = async (snapshot,coreDiameterM,span) => {
  props.clear(); core.scale.setScalar(coreDiameterM);
  for(const p of snapshot.items){
    view.manifests[p.def]={viewerPivot:'bottom'};
    // Fail closed; EditorView's pink fallback is not an admissible reference mesh.
    await view.loadDef(p.def);
    const group=await view._makeProp(p);props.add(group);
  }
  const geometry=meshDiagnostics(props.children,THREE);
  const saved=[];props.traverse(o=>{if(o.isMesh)saved.push([o,o.material]);});
  const rows=[];
  camera.left=-span/2;camera.right=span/2;camera.top=span/2;camera.bottom=-span/2;camera.updateProjectionMatrix();
  const montage=document.createElement('canvas');montage.width=1536;montage.height=1088;
  const mc=montage.getContext('2d');mc.fillStyle='#1b2330';mc.fillRect(0,0,1536,1088);
  for(let i=0;i<directions.length;i++){
    camera.position.copy(toThreePosition(directions[i])).multiplyScalar(span*3);
    camera.up.set(0,1,0);if(Math.abs(directions[i][1])>.5)camera.up.set(0,0,-1);
    camera.lookAt(0,0,0);camera.updateMatrixWorld(true);
    core.material=coreMaterial;props.visible=true;
    for(const [o,m] of saved)o.material=m;
    const beauty=capture();mc.drawImage(view.canvas,(i%3)*512,Math.floor(i/3)*544,512,512);
    mc.fillStyle='#e7eef8';mc.font='18px sans-serif';mc.fillText(`N=${snapshot.n}  ${labels[i]}  D=${snapshot.diameterM.toFixed(4)} m  core=${coreDiameterM.toFixed(4)} m`,(i%3)*512+12,Math.floor(i/3)*544+535);
    core.material=keyMaterial;for(const [o,m] of saved)o.material=occluder(m);
    const mask=capture(),visible=keyPixels();
    props.visible=false;const coreOnly=capture(),baseline=keyPixels();
    if(!baseline)throw new Error('core-only mask has zero pixels');
    rows.push({view:labels[i],beauty,mask,coreOnly,visible,baseline,fraction:visible/baseline});
  }
  core.material=coreMaterial;props.visible=true;for(const [o,m] of saved)o.material=m;
  return {rows,geometry,montage:montage.toDataURL('image/png')};
};
// Hypothetical control only: where each incoming prop stops if its real mesh also stops on already-attached meshes.
// `ballStopM` is the runtime sphere-contact distance for that step; the result is never nearer than it.
window.solveTouchStops = async (items) => {
  const statics=[];const stops=[];
  let reach=0;
  for(const item of items){
    view.manifests[item.def]={viewerPivot:'bottom'};
    await view.loadDef(item.def);
    const group=await view._makeProp({...item,position:[0,0,0]});
    const tris=worldTriangles(group,THREE);
    let movingRadius=0;for(const t of tris)for(const p of t)movingRadius=Math.max(movingRadius,Math.hypot(...p));
    const direction=toThreePosition(item.direction).toArray();
    const moving=tree(tris.map(t=>t.map(p=>p.slice())));
    const far=Math.max(item.ballStopM,reach+movingRadius+.01);
    const contact=firstMeshContact(moving,direction,statics,far,item.ballStopM);
    if(contact.startsInContact)throw new Error('prop starts in contact with attached meshes');
    // 0.1 mm stand-off so "first contact" is not counted as penetration by the surface-intersection diagnostic.
    const stop=Math.max(item.ballStopM,contact.distance+1e-4);
    stops.push({stopM:stop,ballStopM:item.ballStopM,stoppedByAttached:contact.hit&&contact.distance+1e-4>item.ballStopM});
    const offset=direction.map(x=>x*stop);
    const placed=tris.map(t=>shift(t,offset));
    for(const t of placed)for(const p of t)reach=Math.max(reach,Math.hypot(...p));
    statics.push(tree(placed));
  }
  return stops;
};
window.__attachReady = true;
