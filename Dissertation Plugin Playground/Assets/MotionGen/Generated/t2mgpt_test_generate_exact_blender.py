import json
from pathlib import Path

import bpy
from mathutils import Vector

JSON_PATH = Path(r"C:\Users\jack\OneDrive\Desktop\Coding\Dissertation\Dissertation Plugin Playground\Assets\MotionGen\Generated\t2mgpt_test_generate_exact.json")
COLLECTION_NAME = "T2M_GPT_TestGenerate_Exact"
JOINT_RADIUS = 0.035
BONE_RADIUS = 0.018
LOCK_ROOT_XY = True

EDGES = [
    (0, 1), (1, 4), (4, 7), (7, 10),
    (0, 2), (2, 5), (5, 8), (8, 11),
    (0, 3), (3, 6), (6, 9), (9, 12), (12, 15),
    (9, 13), (13, 16), (16, 18), (18, 20),
    (9, 14), (14, 17), (17, 19), (19, 21),
]

CHAIN_COLORS = {
    "left_leg": (0.12, 0.36, 0.95, 1.0),
    "right_leg": (0.95, 0.18, 0.18, 1.0),
    "spine": (0.08, 0.08, 0.08, 1.0),
    "left_arm": (0.05, 0.82, 0.82, 1.0),
    "right_arm": (1.0, 0.56, 0.10, 1.0),
    "joint": (0.20, 0.55, 0.95, 1.0),
}

EDGE_GROUPS = {
    (0, 1): "left_leg", (1, 4): "left_leg", (4, 7): "left_leg", (7, 10): "left_leg",
    (0, 2): "right_leg", (2, 5): "right_leg", (5, 8): "right_leg", (8, 11): "right_leg",
    (0, 3): "spine", (3, 6): "spine", (6, 9): "spine", (9, 12): "spine", (12, 15): "spine",
    (9, 13): "left_arm", (13, 16): "left_arm", (16, 18): "left_arm", (18, 20): "left_arm",
    (9, 14): "right_arm", (14, 17): "right_arm", (17, 19): "right_arm", (19, 21): "right_arm",
}


def load_motion(path: Path):
    motion = json.loads(path.read_text(encoding="utf-8"))
    frames = motion["frames"]
    fps = int(motion.get("fps") or 20)
    joint_names = motion.get("jointNames") or [f"joint_{i}" for i in range(len(frames[0]["joints"]))]

    positions = []
    for frame in frames:
        frame_positions = []
        for joint in frame["joints"]:
            frame_positions.append(Vector((joint["x"], joint["z"], joint["y"])))

        if LOCK_ROOT_XY and frame_positions:
            root = frame_positions[0].copy()
            root_offset = Vector((root.x, root.y, 0.0))
            frame_positions = [position - root_offset for position in frame_positions]

        positions.append(frame_positions)

    return fps, joint_names, positions


def ensure_collection(name: str):
    if name in bpy.data.collections:
        old = bpy.data.collections[name]
        for obj in list(old.objects):
            bpy.data.objects.remove(obj, do_unlink=True)
        bpy.data.collections.remove(old)

    collection = bpy.data.collections.new(name)
    bpy.context.scene.collection.children.link(collection)
    return collection


def make_material(name: str, color):
    mat = bpy.data.materials.get(name)
    if mat is None:
        mat = bpy.data.materials.new(name=name)
        mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    if bsdf is not None:
        base_color_input = bsdf.inputs.get("Base Color")
        if base_color_input is not None:
            base_color_input.default_value = color
        roughness_input = bsdf.inputs.get("Roughness")
        if roughness_input is not None:
            roughness_input.default_value = 0.15
    return mat


def create_joint_object(name: str, collection, material):
    bpy.ops.mesh.primitive_uv_sphere_add(radius=JOINT_RADIUS, location=(0, 0, 0))
    obj = bpy.context.object
    obj.name = name
    obj.data.materials.clear()
    obj.data.materials.append(material)
    if obj.name not in collection.objects:
        collection.objects.link(obj)

    scene_root = bpy.context.scene.collection
    if obj.name in scene_root.objects:
        scene_root.objects.unlink(obj)
    return obj


def create_bone_object(name: str, collection, material):
    bpy.ops.mesh.primitive_cylinder_add(radius=BONE_RADIUS, depth=1.0, location=(0, 0, 0))
    obj = bpy.context.object
    obj.name = name
    obj.rotation_mode = "QUATERNION"
    obj.data.materials.clear()
    obj.data.materials.append(material)
    if obj.name not in collection.objects:
        collection.objects.link(obj)

    scene_root = bpy.context.scene.collection
    if obj.name in scene_root.objects:
        scene_root.objects.unlink(obj)
    return obj


def orient_segment(obj, start: Vector, end: Vector):
    direction = end - start
    length = direction.length
    if length < 1e-6:
        obj.scale = (1.0, 1.0, 0.0001)
        obj.location = start
        return

    midpoint = (start + end) * 0.5
    rotation = Vector((0.0, 0.0, 1.0)).rotation_difference(direction.normalized())

    obj.location = midpoint
    obj.rotation_mode = "QUATERNION"
    obj.rotation_quaternion = rotation
    obj.scale = (1.0, 1.0, length)


def main():
    fps, joint_names, frames = load_motion(JSON_PATH)

    scene = bpy.context.scene
    scene.render.fps = fps
    scene.frame_start = 1
    scene.frame_end = len(frames)

    collection = ensure_collection(COLLECTION_NAME)
    materials = {name: make_material(f"{COLLECTION_NAME}_{name}", color) for name, color in CHAIN_COLORS.items()}

    joint_objects = []
    for idx, joint_name in enumerate(joint_names):
        obj = create_joint_object(f"joint_{idx:02d}_{joint_name}", collection, materials["joint"])
        joint_objects.append(obj)

    edge_objects = {}
    for start_idx, end_idx in EDGES:
        group = EDGE_GROUPS[(start_idx, end_idx)]
        obj = create_bone_object(f"bone_{start_idx:02d}_{end_idx:02d}", collection, materials[group])
        edge_objects[(start_idx, end_idx)] = obj

    for frame_index, frame_positions in enumerate(frames, start=1):
        for joint_idx, position in enumerate(frame_positions):
            obj = joint_objects[joint_idx]
            obj.location = position
            obj.keyframe_insert(data_path="location", frame=frame_index)

        for edge, obj in edge_objects.items():
            start = frame_positions[edge[0]]
            end = frame_positions[edge[1]]
            orient_segment(obj, start, end)
            obj.keyframe_insert(data_path="location", frame=frame_index)
            obj.keyframe_insert(data_path="rotation_quaternion", frame=frame_index)
            obj.keyframe_insert(data_path="scale", frame=frame_index)

    print(f"Imported {len(frames)} exact frames from {JSON_PATH}")


main()
