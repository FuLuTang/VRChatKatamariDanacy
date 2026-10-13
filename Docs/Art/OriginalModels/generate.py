import bpy, math, json, os, sys
from mathutils import Vector

OUT = os.environ.get('OUT_DIR', r'D:\VRChatKatamariPreview\OriginalModels')
NAME = os.environ.get('MODEL_NAME', 'ball_core')
os.makedirs(OUT, exist_ok=True)
bpy.ops.object.select_all(action='SELECT'); bpy.ops.object.delete(use_global=False)
for d in bpy.data.materials: bpy.data.materials.remove(d)

def mat(name, color):
 m=bpy.data.materials.new(name); m.diffuse_color=(*color,1); return m
A=mat('Placeholder_Matte_Teal',(0.12,.42,.42)); B=mat('Placeholder_Warm',(0.82,.42,.16)); C=mat('Placeholder_Light',(0.8,.78,.65)); D=mat('Placeholder_Dark',(0.12,.15,.16))
def finish(obj,name, material=None):
 obj.name=name; obj.data.name=name+'_Mesh'
 if material: obj.data.materials.append(material)
 return obj
def cube(name, loc, scale, material):
 bpy.ops.mesh.primitive_cube_add(size=1, location=loc); o=bpy.context.object; o.dimensions=scale; bpy.ops.object.transform_apply(location=False, rotation=False, scale=True); return finish(o,name,material)
def cylinder(name, loc, radius, depth, material, verts=32):
 bpy.ops.mesh.primitive_cylinder_add(vertices=verts, radius=radius, depth=depth, location=loc); return finish(bpy.context.object,name,material)
def uv(name,loc,scale,material,seg=20,rings=12):
 bpy.ops.mesh.primitive_uv_sphere_add(segments=seg, ring_count=rings, location=loc); o=bpy.context.object; o.scale=scale; bpy.ops.object.transform_apply(location=False,rotation=False,scale=True); return finish(o,name,material)
objs=[]
if NAME=='ball_core':
 bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=3,radius=.5); o=bpy.context.object
 for v in o.data.vertices:
  n=v.co.normalized(); v.co *= 1+0.012*math.sin(7*n.x+3*n.z)*math.cos(8*n.y)
 # center the mesh exactly on its pivot
 center=sum((v.co for v in o.data.vertices), Vector())/len(o.data.vertices)
 for v in o.data.vertices: v.co -= center
 finish(o,NAME,A); objs=[o]
elif NAME=='carrier_platform':
 o=cylinder(NAME,(0,0,.1),.60,.20,A,48); objs=[o]
 # raised rim + central seat marker (all stable geometry)
 rim=cylinder('carrier_platform_Rim',(0,0,.205),.60,.035,B,48); objs.append(rim)
 seat=cube('carrier_platform_SeatMarker',(0,-.12,.235),(.28,.28,.05),C); objs.append(seat)
 for n,loc in [('SeatAnchor',(0,-.12,.27)),('FollowRigAnchor',(0,.28,.85))]:
  emp=bpy.data.objects.new(n,None); bpy.context.collection.objects.link(emp); emp.location=loc
elif NAME=='exit_button':
 base=cylinder(NAME,(0,0,.25),.23,.50,D,32); objs=[base]
 pl=cylinder('exit_button_Collar',(0,0,.50),.21,.06,B,32); objs.append(pl)
 button=uv('exit_button_Button',(0,0,.62),(.18,.18,.12),B,24,12); objs.append(button)
elif NAME=='ground_cloth_tile':
 # 4m square, gently beveled outer ramp created as bevelled cube; checker top tiles
 base=cube(NAME,(0,0,.055),(4,4,.11),D); bev=base.modifiers.new('soft_ramp_edges','BEVEL'); bev.width=.12; bev.segments=3; bpy.context.view_layer.objects.active=base; bpy.ops.object.modifier_apply(modifier=bev.name); objs=[base]
 for ix in range(8):
  for iy in range(8):
   o=cube('cloth_checker_%02d_%02d'%(ix,iy),(-1.75+ix*.5,-1.75+iy*.5,.116),(.49,.49,.012),A if (ix+iy)%2==0 else C); objs.append(o)
elif NAME=='item_apple':
 objs=[uv(NAME,(0,0,.04),(.04,.04,.04),B,20,12),cube('item_apple_Stem',(0,0,.085),(.006,.006,.02),D)]
elif NAME=='item_cup':
 # cup body and open top rim impression
 objs=[cylinder(NAME,(0,0,.05),.035,.10,C,24),cylinder('item_cup_Rim',(0,0,.102),.038,.008,B,24)]
elif NAME=='item_plate':
 objs=[cylinder(NAME,(0,0,.008),.07,.016,C,32),cylinder('item_plate_Well',(0,0,.017),.045,.006,A,32)]
elif NAME=='item_sugar_cube': objs=[cube(NAME,(0,0,.01),(.02,.02,.02),C)]
elif NAME=='beetle_simple':
 objs=[uv(NAME,(0,0,.12),(.15,.09,.07),D,24,12),uv('beetle_simple_Head',(0,.12,.11),(.055,.05,.05),A,16,10)]
 for x in [-.055,.055]:
  for y in [-.03,.03]: objs.append(cube('beetle_simple_Leg',(x,y,.045),(.018,.09,.018),D))
else: raise Exception('unknown model '+NAME)
# Root empty as origin/pivot carrier; mesh objects kept individually and grouped under root
root=bpy.data.objects.new(NAME+'_Root',None); bpy.context.collection.objects.link(root)
for o in objs: o.parent=root
# For furniture and items origin is already bottom-center; ball origin remains center.
# force mesh normals and run topology diagnostics
for o in objs:
 if o.type=='MESH':
  bpy.context.view_layer.objects.active=o; o.select_set(True); bpy.ops.object.mode_set(mode='EDIT'); bpy.ops.mesh.select_all(action='SELECT'); bpy.ops.mesh.normals_make_consistent(inside=False); bpy.ops.object.mode_set(mode='OBJECT'); o.select_set(False)
# aggregate bounds and counts
meshes=[o for o in objs if o.type=='MESH']; verts=sum(len(o.data.vertices) for o in meshes); tris=sum(sum(max(0,len(p.vertices)-2) for p in o.data.polygons) for o in meshes)
issues=[]
for o in meshes:
 bm=None
 import bmesh
 bm=bmesh.new(); bm.from_mesh(o.data); nonman=sum(1 for e in bm.edges if not e.is_manifold); zero=sum(1 for f in bm.faces if f.calc_area()<1e-12); bm.free()
 if nonman or zero: issues.append({'object':o.name,'non_manifold_edges':nonman,'zero_area_faces':zero})
# world bounds from transformed mesh corners
pts=[o.matrix_world @ Vector(c) for o in meshes for c in o.bound_box]
mins=[min(p[i] for p in pts) for i in range(3)]; maxs=[max(p[i] for p in pts) for i in range(3)]
# camera/render three views as one image via generated render layers and pixel copy (side-by-side)
scene=bpy.context.scene; scene.render.engine='BLENDER_EEVEE'; scene.render.resolution_x=480; scene.render.resolution_y=480; scene.render.resolution_percentage=100
scene.render.image_settings.file_format='PNG'; scene.render.film_transparent=False
# neutral world
scene.world.color=(.08,.08,.08)
# hide utility camera/light are generated temporarily
cam_data=bpy.data.cameras.new('PreviewCamera'); cam=bpy.data.objects.new('PreviewCamera',cam_data); bpy.context.collection.objects.link(cam); scene.camera=cam
ld=bpy.data.lights.new('PreviewLight','AREA'); lo=bpy.data.objects.new('PreviewLight',ld); bpy.context.collection.objects.link(lo); lo.location=(4,-5,7); ld.energy=900; ld.size=5
# bounding radius for framing
center=Vector(((mins[0]+maxs[0])/2,(mins[1]+maxs[1])/2,(mins[2]+maxs[2])/2)); span=max(maxs[i]-mins[i] for i in range(3)); dist=max(2,span*2.8)
images=[]
for idx, direction in enumerate([Vector((1,-1,.8)),Vector((0,-1,0)),Vector((0,0,1))]):
 cam.location=center+direction.normalized()*dist; cam.rotation_euler=(center-cam.location).to_track_quat('-Z','Y').to_euler(); cam.data.type='ORTHO'; cam.data.ortho_scale=span*1.35+0.2
 scene.render.filepath=os.path.join(OUT,NAME+'_view%d.png'%idx); bpy.ops.render.render(write_still=True); images.append(bpy.data.images.load(scene.render.filepath,check_existing=False))
# combine images pixels into a 3-panel preview
im=bpy.data.images.new(NAME+'_Preview',width=1440,height=480,alpha=True)
pix=[0.08,0.08,0.08,1.0]*(1440*480)
for j,src in enumerate(images):
 p=list(src.pixels[:])
 for y in range(480):
  for x in range(480):
   si=(y*480+x)*4; di=(y*1440+(j*480+x))*4; pix[di:di+4]=p[si:si+4]
im.pixels[:]=pix; im.filepath_raw=os.path.join(OUT,NAME+'_preview.png'); im.file_format='PNG'; im.save()
for src in images: bpy.data.images.remove(src)
# cleanup camera/light from asset scene before saving/exporting
bpy.data.objects.remove(cam,do_unlink=True); bpy.data.objects.remove(lo,do_unlink=True)
bpy.ops.object.select_all(action='DESELECT')
for o in objs+[root]: o.select_set(True)
bpy.context.view_layer.objects.active=root
blend=os.path.join(OUT,NAME+'.blend'); bpy.ops.wm.save_as_mainfile(filepath=blend)
fbx=os.path.join(OUT,NAME+'.fbx'); bpy.ops.export_scene.fbx(filepath=fbx, use_selection=True, apply_scale_options='FBX_SCALE_UNITS', axis_forward='-Z',axis_up='Y', bake_space_transform=True, object_types={'MESH','EMPTY'})
manifest={'name':NAME,'vertices':verts,'triangles':tris,'materials':sorted(set(m.name for o in meshes for m in o.data.materials if m)),'bounds_world_m':{'min':mins,'max':maxs},'pivot':'ball_core: 球心; otherwise base bottom-center (temporary)','axes':'Blender Z-up, 1 BU = 1 m; FBX Forward=-Z, Up=Y, apply transform; Unity intended rotation (0,0,0), scale (1,1,1), upright, front +Z','labels':['原创','占位','非最终','尺度 OPEN；尺寸仅为现实参考'],'generation_script':'generate.py','mesh_checks':{'non_manifold_or_zero_area':issues,'normal_recalculation':'performed'},'export_settings':{'axis_forward':'-Z','axis_up':'Y','bake_space_transform':True,'apply_scale_options':'FBX_SCALE_UNITS'}}
with open(os.path.join(OUT,NAME+'.manifest.json'),'w',encoding='utf-8') as f: json.dump(manifest,f,ensure_ascii=False,indent=2)
print(json.dumps(manifest,ensure_ascii=False))
