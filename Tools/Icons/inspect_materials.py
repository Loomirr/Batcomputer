"""Read-only Blender shader graph report for material parity research."""
import bpy
import json
import sys
from pathlib import Path

def value(socket):
    v = getattr(socket, 'default_value', None)
    if v is None or isinstance(v, (str, bool, int, float)):
        return v
    try:
        return list(v)
    except TypeError:
        return str(v)

def tree_data(tree):
    return dict(name=tree.name, nodes=[dict(name=n.name, type=n.type, label=n.label,
        operation=getattr(n, 'operation', None), blend=getattr(n, 'blend_type', None),
        group=n.node_tree.name if n.type == 'GROUP' and n.node_tree else None,
        image=n.image.name if n.type == 'TEX_IMAGE' and n.image else None,
        ramp=dict(interpolation=n.color_ramp.interpolation, elements=[dict(position=e.position, color=list(e.color)) for e in n.color_ramp.elements]) if n.type == 'VALTORGB' else None,
        inputs=[dict(index=i, name=s.name, value=value(s)) for i,s in enumerate(n.inputs)],
        outputs=[dict(index=i, name=s.name, value=value(s)) for i,s in enumerate(n.outputs)]) for n in tree.nodes],
        links=[dict(source=l.from_node.name, source_socket=l.from_socket.name,
                    source_index=list(l.from_node.outputs).index(l.from_socket),
                    target=l.to_node.name, target_socket=l.to_socket.name,
                    target_index=list(l.to_node.inputs).index(l.to_socket)) for l in tree.links])

out = Path(sys.argv[sys.argv.index('--') + 1]); out.parent.mkdir(parents=True, exist_ok=True)
trees = [m.node_tree for m in bpy.data.materials if m.node_tree] + [t for t in bpy.data.node_groups if t.type == 'SHADER']
out.write_text(json.dumps([tree_data(t) for t in trees], indent=2), encoding='utf-8')
print('Wrote', len(trees), 'shader trees to', out)
