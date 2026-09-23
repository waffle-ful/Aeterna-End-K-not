#include <jni.h>
#include <android/log.h>
#include <vector>

#define LOG_TAG "FusionBridge"
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

namespace
{
    void throwIllegalArgument(JNIEnv *env, const char *message)
    {
        jclass cls = env->FindClass("java/lang/IllegalArgumentException");
        if (cls)
        {
            env->ThrowNew(cls, message);
            env->DeleteLocalRef(cls);
        }
    }
}

// Allocates an instance of `cls` without running any constructor (JNI AllocObject).
// The class is initialized by the runtime if it was not already.
extern "C" JNIEXPORT jobject JNICALL
Java_dev_allofus_fusioncore_bridge_FusionJni_allocObject(JNIEnv *env, jclass, jclass cls)
{
    if (!cls)
    {
        throwIllegalArgument(env, "allocObject: class is null");
        return nullptr;
    }
    return env->AllocObject(cls);
}

// Invokes exactly the given java.lang.reflect.Method / Constructor declared on `cls` on
// `receiver`, bypassing virtual dispatch (JNI CallNonvirtualVoidMethodA). Every argument
// must be a reference; primitives are not supported on purpose, the two call sites
// (Activity.onCreate(Bundle) and UnityPlayer.<init>(Context, IUnityPlayerLifecycleEvents))
// take only objects. An exception thrown by the callee stays pending and surfaces in Java.
extern "C" JNIEXPORT void JNICALL
Java_dev_allofus_fusioncore_bridge_FusionJni_invokeNonvirtualVoid(JNIEnv *env, jclass, jobject receiver,
                                                                  jclass cls, jobject executable,
                                                                  jobjectArray args)
{
    if (!receiver || !cls || !executable)
    {
        throwIllegalArgument(env, "invokeNonvirtualVoid: receiver, class and executable are required");
        return;
    }
    jmethodID method = env->FromReflectedMethod(executable);
    if (!method)
    {
        if (!env->ExceptionCheck())
        {
            throwIllegalArgument(env, "invokeNonvirtualVoid: executable did not resolve to a method id");
        }
        return;
    }
    jsize count = args ? env->GetArrayLength(args) : 0;
    std::vector<jvalue> values(static_cast<size_t>(count));
    for (jsize i = 0; i < count; i++)
    {
        values[static_cast<size_t>(i)].l = env->GetObjectArrayElement(args, i);
    }
    env->CallNonvirtualVoidMethodA(receiver, cls, method, values.data());
    for (jsize i = 0; i < count; i++)
    {
        jobject ref = values[static_cast<size_t>(i)].l;
        if (ref)
        {
            env->DeleteLocalRef(ref);
        }
    }
}
