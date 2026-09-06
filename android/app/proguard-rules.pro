# MiniDrop R8 规则
# Room / WorkManager / OkHttp / Compose 自带 consumer 规则，无需重复添加。

# kotlinx.serialization：Json.parseToJsonElement 为手动解析，无 @Serializable 反射类，
# 但保留注解与序列化器查找以防未来启用
-keepattributes *Annotation*, InnerClasses, Signature
-dontnote kotlinx.serialization.**
-keepclassmembers class kotlinx.serialization.json.** {
    *** Companion;
}
-keep,includedescriptorclasses class com.minidrop.app.**$$serializer { *; }
-keepclassmembers class com.minidrop.app.** {
    *** Companion;
}

# 崩溃堆栈可读性（保留行号，映射文件随构建产出）
-keepattributes SourceFile,LineNumberTable
-renamesourcefileattribute SourceFile
