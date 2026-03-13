import sys
from contextlib import contextmanager
from pathlib import Path

BACKEND = Path(__file__).resolve().parents[1]
T2MGPT_DIR = BACKEND / "models" / "T2M-GPT"
MOMASK_DIR = BACKEND / "models" / "MoMask"

_CONFLICTING_MODULE_PREFIXES = (
    "models",
    "utils",
    "options",
    "common",
    "data",
    "motion_loaders",
    "visualization",
)


def purge_conflicting_modules() -> None:
    to_remove = [
        module_name
        for module_name in sys.modules
        if any(
            module_name == prefix or module_name.startswith(f"{prefix}.")
            for prefix in _CONFLICTING_MODULE_PREFIXES
        )
    ]
    for module_name in to_remove:
        sys.modules.pop(module_name, None)


@contextmanager
def isolated_model_path(model_dir: Path, blocked_dirs: tuple[Path, ...] = ()) -> None:
    original_path = sys.path.copy()
    blocked_prefixes = tuple(str(path) for path in blocked_dirs)
    sys.path = [
        path
        for path in sys.path
        if path != str(model_dir) and not any(path.startswith(prefix) for prefix in blocked_prefixes)
    ]
    sys.path.insert(0, str(model_dir))
    purge_conflicting_modules()
    try:
        yield
    finally:
        sys.path = original_path


@contextmanager
def t2mgpt_path() -> None:
    with isolated_model_path(T2MGPT_DIR, blocked_dirs=(MOMASK_DIR,)):
        yield


@contextmanager
def momask_path() -> None:
    with isolated_model_path(MOMASK_DIR, blocked_dirs=(T2MGPT_DIR,)):
        yield
