"""Convert the selected free festival GLBs with Blender's bundled Python.

Run from the repository root:
  blender --background --factory-startup --python Tools~/Art/convert_festival_assets.py

No Unity glTF package is required. Sources stay under Tools~; only the cleaned
FBXs and a palette manifest are exported to Assets. Repeated material entries
are consolidated by colour, with one mesh per colour in each logical model.
"""
import bpy
import json
import math
import shutil
from pathlib import Path
from mathutils import Matrix, Vector


ROOT = Path(__file__).resolve().parents[2]
SOURCES = ROOT / "Tools~" / "Art" / "FestivalSource" / "1DesignTool"
OUTPUT = ROOT / "Assets" / "Art" / "Festival" / "Models"
PALETTE = {}
REPORT = []


def srgb(channel):
    return 12.92 * channel if channel <= 0.0031308 else 1.055 * channel ** (1 / 2.4) - 0.055


def material_colour(material):
    if material and material.use_nodes:
        for node in material.node_tree.nodes:
            if node.type == "BSDF_PRINCIPLED":
                return list(node.inputs["Base Color"].default_value)
    return list(material.diffuse_color) if material else [0.5, 0.5, 0.5, 1]


def palette_material(source):
    # The food trucks use a small gradient atlas as well as plain materials.
    # Retain that authored texture rather than reducing it to one sampled colour.
    if source and source.use_nodes:
        images = [node.image for node in source.node_tree.nodes if node.type == "TEX_IMAGE" and node.image]
        if images:
            key = "Festival_TruckPalette"
            existing = bpy.data.materials.get(key)
            if not existing:
                existing = bpy.data.materials.new(key)
                existing.use_nodes = True
                shader = existing.node_tree.nodes.get("Principled BSDF")
                shader.inputs["Roughness"].default_value = 0.85
                image_node = existing.node_tree.nodes.new("ShaderNodeTexImage")
                image_node.image = images[0]
                existing.node_tree.links.new(image_node.outputs["Color"], shader.inputs["Base Color"])
            texture_dir = OUTPUT.parent / "Textures"
            texture_dir.mkdir(parents=True, exist_ok=True)
            original_image = Path(bpy.path.abspath(images[0].filepath))
            texture_path = texture_dir / "TruckPalette.png"
            if not original_image.exists():
                original_image = next((ROOT / "Tools~" / "Art" / "FestivalSource" / "FoodTrucks" / "Models").rglob("*.png"))
            shutil.copy2(original_image, texture_path)
            PALETTE[key] = {"name": key, "r": 1, "g": 1, "b": 1, "a": 1, "texture": "TruckPalette.png"}
            return existing
    rgba = material_colour(source)
    channels = [max(0, min(255, round(srgb(max(0, c)) * 255))) for c in rgba[:3]]
    key = "Festival_" + "".join(f"{c:02X}" for c in channels)
    existing = bpy.data.materials.get(key)
    if not existing:
        existing = bpy.data.materials.new(key)
        existing.diffuse_color = rgba
        existing.use_nodes = True
        shader = existing.node_tree.nodes.get("Principled BSDF")
        shader.inputs["Base Color"].default_value = rgba
        shader.inputs["Roughness"].default_value = 0.85
        shader.inputs["Metallic"].default_value = 0
    PALETTE[key] = {"name": key, "r": channels[0] / 255, "g": channels[1] / 255,
                    "b": channels[2] / 255, "a": rgba[3]}
    return existing


def descendants(obj):
    return [obj] + list(obj.children_recursive)


def bounds(objects):
    points = [obj.matrix_world @ vertex.co for obj in objects for vertex in obj.data.vertices]
    return Vector(tuple(min(p[i] for p in points) for i in range(3))), Vector(tuple(max(p[i] for p in points) for i in range(3)))


def export_model(source_name, output_name, groups=None, add_tower_base=False, input_path=None):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.context.scene.unit_settings.system = "METRIC"
    bpy.context.scene.unit_settings.scale_length = 1
    if input_path:
        bpy.ops.import_scene.fbx(filepath=str(input_path))
    else:
        bpy.ops.import_scene.gltf(filepath=str(SOURCES / (source_name + ".glb")))
    all_meshes = [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]
    if groups:
        roots = [obj for obj in bpy.context.scene.objects if obj.name in groups]
        if len(roots) != len(groups):
            raise RuntimeError(f"Missing source groups for {output_name}: {groups}")
        selected = {obj for root in roots for obj in descendants(root) if obj.type == "MESH"}
        meshes = [obj for obj in all_meshes if obj in selected]
    else:
        meshes = all_meshes
    if not meshes:
        raise RuntimeError(f"No mesh data for {output_name}")
    for obj in list(bpy.context.scene.objects):
        if obj.type == "MESH" and obj not in meshes:
            bpy.data.objects.remove(obj, do_unlink=True)
    # Bake the source hierarchy to world space before discarding its empty nodes.
    for obj in meshes:
        matrix = obj.matrix_world.copy()
        obj.parent = None
        obj.matrix_world = Matrix.Identity(4)
        obj.data = obj.data.copy()
        obj.data.transform(matrix)
        for index, slot in enumerate(obj.material_slots):
            obj.data.materials[index] = palette_material(slot.material)
    if add_tower_base:
        low, high = bounds(meshes)
        # A narrow pedestal replaces the wide mobile trailer while retaining the
        # source mast's original six-metre height and the simulation's 2 m plot.
        bpy.ops.mesh.primitive_cube_add(size=1, location=((low.x + high.x) / 2, (low.y + high.y) / 2, 0.32))
        base = bpy.context.object
        base.name = "TowerPedestal"
        base.scale = (0.9, 0.9, 0.64)
        bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
        original = bpy.data.materials.new("Tower base grey")
        original.diffuse_color = (0.205079, 0.223228, 0.262251, 1)
        base.data.materials.append(palette_material(original))
        matrix = base.matrix_world.copy()
        base.matrix_world = Matrix.Identity(4)
        base.data.transform(matrix)
        meshes.append(base)
    for obj in list(bpy.context.scene.objects):
        if obj.type != "MESH":
            bpy.data.objects.remove(obj, do_unlink=True)
    low, high = bounds(meshes)
    origin = Vector(((low.x + high.x) / 2, (low.y + high.y) / 2, low.z))
    for obj in meshes:
        for vertex in obj.data.vertices:
            vertex.co -= origin
    bpy.ops.object.select_all(action="DESELECT")
    for obj in meshes:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    bpy.ops.object.join()
    joined = bpy.context.object
    # Join reuses shared palette materials; separate removes the many original
    # per-part draw calls without smoothing away the authored flat normals.
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.mesh.separate(type="MATERIAL")
    bpy.ops.object.mode_set(mode="OBJECT")
    exported = [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]
    for obj in exported:
        obj.name = output_name + "_" + (obj.data.materials[0].name if obj.data.materials else "Mesh")
    OUTPUT.mkdir(parents=True, exist_ok=True)
    path = OUTPUT / (output_name + ".fbx")
    bpy.ops.export_scene.fbx(filepath=str(path), use_selection=True, object_types={"MESH"},
                             global_scale=1, apply_unit_scale=True, apply_scale_options="FBX_SCALE_UNITS",
                             axis_forward="-Z", axis_up="Y", use_mesh_modifiers=True,
                             mesh_smooth_type="OFF", add_leaf_bones=False, bake_anim=False)
    size = high - low
    for obj in exported:
        obj.data.calc_loop_triangles()
    REPORT.append({"name": output_name, "source": source_name,
                   "width": round(size.x, 5), "height": round(size.z, 5), "depth": round(size.y, 5),
                   "meshes": len(exported), "triangles": sum(len(obj.data.loop_triangles) for obj in exported)})
    print("FESTIVAL_EXPORT " + json.dumps(REPORT[-1]))


export_model("wristband-booth", "WristbandBooth")
export_model("small-stage", "SmallStage")
export_model("small-stage", "EventCanopy", ["truss", "roof", "bunting"])
export_model("speakers", "Speakers")
export_model("portable-toilets", "PortableToilet")
export_model("food-row", "FoodStall01", ["gazebo"])
export_model("food-row", "FoodStall02", ["shack"])
export_model("food-row", "FoodStall03", ["trailer"])
export_model("lights", "LightTower", ["mast", "head", "lamp_1", "lamp_2", "lamp_3", "lamp_4"], True)
export_model("lights", "Generator", ["generator"])
truck_sources = ROOT / "Tools~" / "Art" / "FestivalSource" / "FoodTrucks" / "Models"
for filename, model_name in [("burger truck.fbx", "FoodTruckBurger"), ("donut truck.fbx", "FoodTruckDonut")]:
    matches = list(truck_sources.rglob(filename))
    if matches:
        export_model("designersoup/" + filename, model_name, input_path=matches[0])

manifest = {"version": 1, "models": REPORT, "palette": sorted(PALETTE.values(), key=lambda item: item["name"])}
(OUTPUT.parent / "import-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
print("FESTIVAL_EXPORT_COMPLETE " + str(len(REPORT)) + " models")
