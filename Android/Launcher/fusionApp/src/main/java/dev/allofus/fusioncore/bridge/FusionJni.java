package dev.allofus.fusioncore.bridge;

/** JNI entry points of libfusionbridge.so (src/main/jni/bridge/bridge.cpp). */
final class FusionJni {
    static {
        System.loadLibrary("fusionbridge");
    }

    private FusionJni() {
    }

    /** Allocates an instance of {@code cls} without running any constructor. */
    static native Object allocObject(Class<?> cls);

    /**
     * Invokes exactly {@code executable} (a Method or Constructor declared on {@code cls})
     * on {@code receiver}, bypassing virtual dispatch. All arguments must be references.
     */
    static native void invokeNonvirtualVoid(Object receiver, Class<?> cls, Object executable, Object[] args);
}
