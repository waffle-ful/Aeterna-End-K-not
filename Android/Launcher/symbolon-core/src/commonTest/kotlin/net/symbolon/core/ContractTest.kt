package net.symbolon.core

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class ContractTest {
    @Test
    fun codePrefixesEndWithDot() {
        assertTrue(Contract.MAP_CODE_PREFIX.endsWith("."))
        assertTrue(Contract.ROLE_CODE_PREFIX.endsWith("."))
    }

    @Test
    fun mapFileSuffixIsJson() {
        assertEquals(".json", Contract.MAP_FILE_SUFFIX.takeLast(5))
    }
}
