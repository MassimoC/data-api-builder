// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using Azure.DataApiBuilder.Auth;
using Azure.DataApiBuilder.Config.ObjectModel;
using Azure.DataApiBuilder.Service.Authorization;
using Azure.DataApiBuilder.Service.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Azure.DataApiBuilder.Service.Tests.Authorization
{
    [TestClass]
    public class AuthorizationResolverUnitTests
    {
        private const string TEST_ENTITY = "SampleEntity";
        private const string TEST_ROLE = "Writer";
        private const EntityActionOperation TEST_OPERATION = EntityActionOperation.Create;
        private const string TEST_AUTHENTICATION_TYPE = "TestAuth";
        private const string TEST_CLAIMTYPE_NAME = "TestName";

        #region Role Context Tests
        /// <summary>
        /// When the client role header is present, validates result when
        /// Role is in ClaimsPrincipal.Roles -> VALID
        /// Role is NOT in ClaimsPrincipal.Roles -> INVALID
        /// </summary>
        [DataTestMethod]
        [DataRow("Reader", true, true)]
        [DataRow("Reader", false, false)]
        public void ValidRoleContext_Simple(string clientRoleHeaderValue, bool userIsInRole, bool expected)
        {
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig();
            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            Mock<HttpContext> context = new();
            context.SetupGet(x => x.Request.Headers[AuthorizationResolver.CLIENT_ROLE_HEADER]).Returns(clientRoleHeaderValue);
            context.Setup(x => x.User.IsInRole(clientRoleHeaderValue)).Returns(userIsInRole);
            context.Setup(x => x.User.Identity!.IsAuthenticated).Returns(true);

            Assert.AreEqual(authZResolver.IsValidRoleContext(context.Object), expected);
        }

        [TestMethod("Role header has no value")]
        public void RoleHeaderEmpty()
        {
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig();
            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            Mock<HttpContext> context = new();
            context.SetupGet(x => x.Request.Headers[AuthorizationResolver.CLIENT_ROLE_HEADER]).Returns(StringValues.Empty);
            bool expected = false;

            Assert.AreEqual(authZResolver.IsValidRoleContext(context.Object), expected);
        }

        [TestMethod("Role header has multiple values")]
        public void RoleHeaderDuplicated()
        {
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig();
            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            Mock<HttpContext> context = new();
            StringValues multipleValuesForHeader = new(new string[] { "Reader", "Writer" });
            context.SetupGet(x => x.Request.Headers[AuthorizationResolver.CLIENT_ROLE_HEADER]).Returns(multipleValuesForHeader);
            context.Setup(x => x.User.IsInRole("Reader")).Returns(true);
            bool expected = false;
            Assert.AreEqual(authZResolver.IsValidRoleContext(context.Object), expected);
        }

        [TestMethod("Role header is missing")]
        public void NoRoleHeader_RoleContextTest()
        {
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig();
            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            Mock<HttpContext> context = new();
            context.SetupGet(x => x.Request.Headers[AuthorizationResolver.CLIENT_ROLE_HEADER]).Returns(StringValues.Empty);
            bool expected = false;

            Assert.AreEqual(authZResolver.IsValidRoleContext(context.Object), expected);
        }
        #endregion

        #region Role and Operation on Entity Tests

        /// <summary>
        /// Tests the AreRoleAndOperationDefinedForEntity stage of authorization.
        /// Request operation is defined for role -> VALID
        /// Request operation not defined for role (role has 0 defined operations)
        ///     Ensures method short circuits in circumstances role is not defined -> INVALID
        /// Request operation does not match an operation defined for role (role has >=1 defined operation) -> INVALID
        /// </summary>
        [DataTestMethod]
        [DataRow("Writer", EntityActionOperation.Create, "Writer", EntityActionOperation.Create, true)]
        [DataRow("Reader", EntityActionOperation.Create, "Reader", EntityActionOperation.None, false)]
        [DataRow("Writer", EntityActionOperation.Create, "Writer", EntityActionOperation.Update, false)]
        public void AreRoleAndOperationDefinedForEntityTest(
            string configRole,
            EntityActionOperation configOperation,
            string roleName,
            EntityActionOperation operation,
            bool expected)
        {
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: configRole,
                operation: configOperation);
            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            // Mock Request Values
            Assert.AreEqual(expected, authZResolver.AreRoleAndOperationDefinedForEntity(AuthorizationHelpers.TEST_ENTITY, roleName, operation));
        }

        /// <summary>
        /// Test that wildcard operation are expanded to explicit operations.
        /// Verifies that internal data structure are created correctly.
        /// </summary>
        [TestMethod("Wildcard operation is expanded to all valid operations")]
        public void TestWildcardOperation()
        {
            List<string> expectedRoles = new() { AuthorizationHelpers.TEST_ROLE };

            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.All);

            // Override the permission operations to be a list of operations for wildcard
            // instead of a list of objects created by readAction, updateAction
            Entity entity = runtimeConfig.Entities[AuthorizationHelpers.TEST_ENTITY];
            entity = entity with { Permissions = new[] { new EntityPermission(AuthorizationHelpers.TEST_ROLE, new EntityAction[] { new(EntityActionOperation.All, null, new(null, null)) }) } };
            runtimeConfig = runtimeConfig with { Entities = new(new Dictionary<string, Entity> { { AuthorizationHelpers.TEST_ENTITY, entity } }) };

            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            // There should not be a wildcard operation in AuthorizationResolver.EntityPermissionsMap
            Assert.IsFalse(authZResolver.AreRoleAndOperationDefinedForEntity(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                EntityActionOperation.All));

            // The wildcard operation should be expanded to all the explicit operations.
            foreach (EntityActionOperation operation in EntityAction.ValidPermissionOperations)
            {
                Assert.IsTrue(authZResolver.AreRoleAndOperationDefinedForEntity(
                    AuthorizationHelpers.TEST_ENTITY,
                    AuthorizationHelpers.TEST_ROLE,
                    operation));

                IEnumerable<string> actualRolesForCol1 = authZResolver.GetRolesForField(AuthorizationHelpers.TEST_ENTITY, "col1", operation);

                CollectionAssert.AreEquivalent(expectedRoles, actualRolesForCol1.ToList());

                IEnumerable<string> actualRolesForOperation = IAuthorizationResolver.GetRolesForOperation(
                    AuthorizationHelpers.TEST_ENTITY,
                    operation,
                    authZResolver.EntityPermissionsMap);
                CollectionAssert.AreEquivalent(expectedRoles, actualRolesForOperation.ToList());
            }

            // Validate that the authorization check fails because the operations are invalid.
            Assert.IsFalse(authZResolver.AreRoleAndOperationDefinedForEntity(AuthorizationHelpers.TEST_ENTITY, TEST_ROLE, EntityActionOperation.Insert));
            Assert.IsFalse(authZResolver.AreRoleAndOperationDefinedForEntity(AuthorizationHelpers.TEST_ENTITY, TEST_ROLE, EntityActionOperation.Upsert));
        }

        /// <summary>
        /// Verify that the internal data structure is created correctly when we have
        /// Two roles for the same entity with different permission.
        /// readOnlyRole - Read permission only for col1 and no policy.
        /// readAndUpdateRole - read and update permission for col1 and no policy.
        /// </summary>
        [TestMethod]
        public void TestRoleAndOperationCombination()
        {
            const string READ_ONLY_ROLE = "readOnlyRole";
            const string READ_AND_UPDATE_ROLE = "readAndUpdateRole";

            EntityActionFields fieldsForRole = new(
                Include: new HashSet<string> { "col1" },
                Exclude: new());

            EntityAction readAction = new(
                Action: EntityActionOperation.Read,
                Fields: fieldsForRole,
                Policy: new(null, null));

            EntityAction updateAction = new(
                Action: EntityActionOperation.Update,
                Fields: fieldsForRole,
                Policy: new(null, null));

            EntityPermission readOnlyPermission = new(
                Role: READ_ONLY_ROLE,
                Actions: new[] { readAction });

            EntityPermission readAndUpdatePermission = new(
            Role: READ_AND_UPDATE_ROLE,
            Actions: new[] { readAction, updateAction });

            EntityPermission[] permissions = new EntityPermission[] { readOnlyPermission, readAndUpdatePermission };

            RuntimeConfig runtimeConfig = BuildTestRuntimeConfig(permissions, TEST_ENTITY);

            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            // Verify that read only role has permission for read and nothing else.
            Assert.IsTrue(authZResolver.AreRoleAndOperationDefinedForEntity(
                AuthorizationHelpers.TEST_ENTITY,
                READ_ONLY_ROLE,
                EntityActionOperation.Read));
            Assert.IsFalse(authZResolver.AreRoleAndOperationDefinedForEntity(
                AuthorizationHelpers.TEST_ENTITY,
                READ_ONLY_ROLE,
                EntityActionOperation.Update));
            Assert.IsFalse(authZResolver.AreRoleAndOperationDefinedForEntity(
                AuthorizationHelpers.TEST_ENTITY,
                READ_ONLY_ROLE,
                EntityActionOperation.Create));
            Assert.IsFalse(authZResolver.AreRoleAndOperationDefinedForEntity(
                AuthorizationHelpers.TEST_ENTITY,
                READ_ONLY_ROLE,
                EntityActionOperation.Delete));

            // Verify that read only role has permission for read/update and nothing else.
            Assert.IsTrue(authZResolver.AreRoleAndOperationDefinedForEntity(
                AuthorizationHelpers.TEST_ENTITY,
                READ_AND_UPDATE_ROLE,
                EntityActionOperation.Read));
            Assert.IsTrue(authZResolver.AreRoleAndOperationDefinedForEntity(
                AuthorizationHelpers.TEST_ENTITY,
                READ_AND_UPDATE_ROLE,
                EntityActionOperation.Update));
            Assert.IsFalse(authZResolver.AreRoleAndOperationDefinedForEntity(
                AuthorizationHelpers.TEST_ENTITY,
                READ_AND_UPDATE_ROLE,
                EntityActionOperation.Create));
            Assert.IsFalse(authZResolver.AreRoleAndOperationDefinedForEntity(
                AuthorizationHelpers.TEST_ENTITY,
                READ_AND_UPDATE_ROLE,
                EntityActionOperation.Delete));

            List<string> expectedRolesForRead = new() { READ_ONLY_ROLE, READ_AND_UPDATE_ROLE };
            List<string> expectedRolesForUpdate = new() { READ_AND_UPDATE_ROLE };

            IEnumerable<string> actualReadRolesForCol1 = authZResolver.GetRolesForField(
                AuthorizationHelpers.TEST_ENTITY,
                "col1",
                EntityActionOperation.Read);
            CollectionAssert.AreEquivalent(expectedRolesForRead, actualReadRolesForCol1.ToList());
            IEnumerable<string> actualUpdateRolesForCol1 = authZResolver.GetRolesForField(
                AuthorizationHelpers.TEST_ENTITY,
                "col1",
                EntityActionOperation.Update);
            CollectionAssert.AreEquivalent(expectedRolesForUpdate, actualUpdateRolesForCol1.ToList());

            IEnumerable<string> actualRolesForRead = IAuthorizationResolver.GetRolesForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                EntityActionOperation.Read,
                authZResolver.EntityPermissionsMap);
            CollectionAssert.AreEquivalent(expectedRolesForRead, actualRolesForRead.ToList());
            IEnumerable<string> actualRolesForUpdate = IAuthorizationResolver.GetRolesForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                EntityActionOperation.Update,
                authZResolver.EntityPermissionsMap);
            CollectionAssert.AreEquivalent(expectedRolesForUpdate, actualRolesForUpdate.ToList());
        }

        /// <summary>
        /// Test to validate that the permissions for the system role "authenticated" are derived the permissions of
        /// the system role "anonymous" when authenticated role is not defined, but anonymous role is defined.
        /// </summary>
        [TestMethod]
        public void TestAuthenticatedRoleWhenAnonymousRoleIsDefined()
        {
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationResolver.ROLE_ANONYMOUS,
                operation: EntityActionOperation.Create);

            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            foreach (EntityActionOperation operation in EntityAction.ValidPermissionOperations)
            {
                if (operation is EntityActionOperation.Create)
                {
                    // Create operation should be defined for anonymous role.
                    Assert.IsTrue(authZResolver.AreRoleAndOperationDefinedForEntity(
                        AuthorizationHelpers.TEST_ENTITY,
                        AuthorizationResolver.ROLE_ANONYMOUS,
                        operation));

                    // Create operation should be defined for authenticated role as well,
                    // because it is defined for anonymous role.
                    Assert.IsTrue(authZResolver.AreRoleAndOperationDefinedForEntity(
                        AuthorizationHelpers.TEST_ENTITY,
                        AuthorizationResolver.ROLE_AUTHENTICATED,
                        operation));
                }
                else
                {
                    // Check that no other operation is defined for the authenticated role to ensure
                    // the authenticated role's permissions match that of the anonymous role's permissions.
                    Assert.IsFalse(authZResolver.AreRoleAndOperationDefinedForEntity(
                        AuthorizationHelpers.TEST_ENTITY,
                        AuthorizationResolver.ROLE_AUTHENTICATED,
                        operation));
                    Assert.IsFalse(authZResolver.AreRoleAndOperationDefinedForEntity(
                        AuthorizationHelpers.TEST_ENTITY,
                        AuthorizationResolver.ROLE_ANONYMOUS,
                        operation));
                }
            }

            // Anonymous role's permissions are copied over for authenticated role only.
            // Assert by checking for an arbitrary role.
            Assert.IsFalse(authZResolver.AreRoleAndOperationDefinedForEntity(AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE, EntityActionOperation.Create));

            // Assert that the create operation has both anonymous, authenticated roles.
            List<string> expectedRolesForCreate = new() { AuthorizationResolver.ROLE_AUTHENTICATED, AuthorizationResolver.ROLE_ANONYMOUS };
            IEnumerable<string> actualRolesForCreate = IAuthorizationResolver.GetRolesForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                EntityActionOperation.Create,
                authZResolver.EntityPermissionsMap);
            CollectionAssert.AreEquivalent(expectedRolesForCreate, actualRolesForCreate.ToList());

            // Assert that the col1 field with create operation has both anonymous, authenticated roles.
            List<string> expectedRolesForCreateCol1 = new() {
                AuthorizationResolver.ROLE_ANONYMOUS,
                AuthorizationResolver.ROLE_AUTHENTICATED };
            IEnumerable<string> actualRolesForCreateCol1 = authZResolver.GetRolesForField(
                AuthorizationHelpers.TEST_ENTITY,
                "col1", EntityActionOperation.Create);
            CollectionAssert.AreEquivalent(expectedRolesForCreateCol1, actualRolesForCreateCol1.ToList());

            // Assert that the col1 field with read operation has no role.
            List<string> expectedRolesForReadCol1 = new();
            IEnumerable<string> actualRolesForReadCol1 = authZResolver.GetRolesForField(
                AuthorizationHelpers.TEST_ENTITY,
                "col1", EntityActionOperation.Read);
            CollectionAssert.AreEquivalent(expectedRolesForReadCol1, actualRolesForReadCol1.ToList());
        }

        /// <summary>
        /// Test to validate that the no permissions for authenticated role are derived when
        /// both anonymous and authenticated role are not defined.
        /// </summary>
        [TestMethod]
        public void TestAuthenticatedRoleWhenAnonymousRoleIsNotDefined()
        {
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create);

            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            // Create operation should be defined for test role.
            Assert.IsTrue(authZResolver.AreRoleAndOperationDefinedForEntity(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                EntityActionOperation.Create));

            // Create operation should not be defined for authenticated role,
            // because neither authenticated nor anonymous role is defined.
            Assert.IsFalse(authZResolver.AreRoleAndOperationDefinedForEntity(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationResolver.ROLE_AUTHENTICATED,
                EntityActionOperation.Create));

            // Assert that the Create operation has only test_role.
            List<string> expectedRolesForCreate = new() { AuthorizationHelpers.TEST_ROLE };
            IEnumerable<string> actualRolesForCreate = IAuthorizationResolver.GetRolesForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                EntityActionOperation.Create,
                authZResolver.EntityPermissionsMap);
            CollectionAssert.AreEquivalent(expectedRolesForCreate, actualRolesForCreate.ToList());

            // Since neither anonymous nor authenticated role is defined for the entity,
            // Create operation would only have the test_role.
            List<string> expectedRolesForCreateCol1 = new() { AuthorizationHelpers.TEST_ROLE };
            IEnumerable<string> actualRolesForCreateCol1 = authZResolver.GetRolesForField(
                AuthorizationHelpers.TEST_ENTITY,
                "col1", EntityActionOperation.Create);
            CollectionAssert.AreEquivalent(expectedRolesForCreateCol1, actualRolesForCreateCol1.ToList());
        }

        /// <summary>
        /// Test to validate that when anonymous and authenticated role are both defined, then
        /// the authenticated role does not derive permissions from anonymous role's permissions.
        /// </summary>
        [TestMethod]
        public void TestAuthenticatedRoleWhenBothAnonymousAndAuthenticatedAreDefined()
        {
            EntityActionFields fieldsForRole = new(
                Include: new HashSet<string> { "col1" },
                Exclude: new());

            EntityAction readAction = new(
                Action: EntityActionOperation.Read,
                Fields: fieldsForRole,
                Policy: new());

            EntityAction updateAction = new(
                Action: EntityActionOperation.Update,
                Fields: fieldsForRole,
                Policy: new());

            EntityPermission authenticatedPermission = new(
                Role: AuthorizationResolver.ROLE_AUTHENTICATED,
                Actions: new[] { readAction });

            EntityPermission anonymousPermission = new(
            Role: AuthorizationResolver.ROLE_ANONYMOUS,
            Actions: new[] { readAction, updateAction });

            EntityPermission[] permissions = new EntityPermission[] { authenticatedPermission, anonymousPermission };
            const string entityName = TEST_ENTITY;
            RuntimeConfig runtimeConfig = BuildTestRuntimeConfig(permissions, entityName);

            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            // Assert that for the role authenticated, only the Read operation is allowed.
            // The Update operation is not allowed even though update is allowed for the role anonymous.
            Assert.IsTrue(authZResolver.AreRoleAndOperationDefinedForEntity(AuthorizationHelpers.TEST_ENTITY,
                AuthorizationResolver.ROLE_AUTHENTICATED, EntityActionOperation.Read));
            Assert.IsTrue(authZResolver.AreRoleAndOperationDefinedForEntity(AuthorizationHelpers.TEST_ENTITY,
                AuthorizationResolver.ROLE_ANONYMOUS, EntityActionOperation.Update));
            Assert.IsFalse(authZResolver.AreRoleAndOperationDefinedForEntity(AuthorizationHelpers.TEST_ENTITY,
                AuthorizationResolver.ROLE_AUTHENTICATED, EntityActionOperation.Delete));

            // Assert that the read operation has both anonymous and authenticated role.
            List<string> expectedRolesForRead = new() {
                AuthorizationResolver.ROLE_ANONYMOUS,
                AuthorizationResolver.ROLE_AUTHENTICATED };
            IEnumerable<string> actualRolesForRead = IAuthorizationResolver.GetRolesForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                EntityActionOperation.Read,
                authZResolver.EntityPermissionsMap);
            CollectionAssert.AreEquivalent(expectedRolesForRead, actualRolesForRead.ToList());

            // Assert that the update operation has only anonymous role.
            List<string> expectedRolesForUpdate = new() { AuthorizationResolver.ROLE_ANONYMOUS };
            IEnumerable<string> actualRolesForUpdate = IAuthorizationResolver.GetRolesForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                EntityActionOperation.Update,
                authZResolver.EntityPermissionsMap);
            CollectionAssert.AreEquivalent(expectedRolesForUpdate, actualRolesForUpdate.ToList());

            // Assert that the col1 field with Read operation has both anonymous and authenticated roles.
            List<string> expectedRolesForReadCol1 = new() {
                AuthorizationResolver.ROLE_ANONYMOUS,
                AuthorizationResolver.ROLE_AUTHENTICATED };
            IEnumerable<string> actualRolesForReadCol1 = authZResolver.GetRolesForField(
                AuthorizationHelpers.TEST_ENTITY,
                "col1", EntityActionOperation.Read);
            CollectionAssert.AreEquivalent(expectedRolesForReadCol1, actualRolesForReadCol1.ToList());

            // Assert that the col1 field with Update operation has only anonymous roles.
            List<string> expectedRolesForUpdateCol1 = new() { AuthorizationResolver.ROLE_ANONYMOUS };
            IEnumerable<string> actualRolesForUpdateCol1 = authZResolver.GetRolesForField(
                AuthorizationHelpers.TEST_ENTITY,
                "col1", EntityActionOperation.Update);
            CollectionAssert.AreEquivalent(expectedRolesForUpdateCol1, actualRolesForUpdateCol1.ToList());
        }

        /// <summary>
        /// Test to validate the AreRoleAndOperationDefinedForEntity method for the case insensitivity of roleName.
        /// For eg. The role Writer is equivalent to wrIter, wRITer, WRITER etc.
        /// </summary>
        /// <param name="configRole">The role configured on the entity.</param>
        /// <param name="operation">The operation configured for the configRole.</param>
        /// <param name="roleNameToCheck">The roleName which is to be checked for the permission.</param>
        [DataTestMethod]
        [DataRow("Writer", EntityActionOperation.Create, "wRiTeR", DisplayName = "role wRiTeR checked against Writer")]
        [DataRow("Reader", EntityActionOperation.Read, "READER", DisplayName = "role READER checked against Reader")]
        [DataRow("Writer", EntityActionOperation.Create, "WrIter", DisplayName = "role WrIter checked against Writer")]
        public void AreRoleAndOperationDefinedForEntityTestForDifferentlyCasedRole(
            string configRole,
            EntityActionOperation operation,
            string roleNameToCheck
            )
        {
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: configRole,
                operation: operation);
            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            // Assert that the roleName is case insensitive.
            Assert.IsTrue(authZResolver.AreRoleAndOperationDefinedForEntity(AuthorizationHelpers.TEST_ENTITY, roleNameToCheck, operation));
        }
        #endregion

        #region Column Tests

        /// <summary>
        /// Tests the authorization stage: Columns defined for operation
        /// Columns are allowed for role
        /// Columns are not allowed for role
        /// Wildcard included and/or excluded columns handling
        /// and assumes request validation has already occurred
        /// </summary>
        [TestMethod("Explicit include columns with no exclusion")]
        public void ExplicitIncludeColumn()
        {
            HashSet<string> includedColumns = new() { "col1", "col2", "col3" };
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create,
                includedCols: includedColumns
                );
            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            Assert.IsTrue(authZResolver.AreColumnsAllowedForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                EntityActionOperation.Create,
                includedColumns));

            // Not allow column.
            Assert.IsFalse(authZResolver.AreColumnsAllowedForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                EntityActionOperation.Create,
                new List<string> { "col4" }));

            // Mix of allow and not allow. Should result in not allow.
            Assert.IsFalse(authZResolver.AreColumnsAllowedForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                EntityActionOperation.Create,
                new List<string> { "col3", "col4" }));

            // Column does not exist 
            Assert.IsFalse(authZResolver.AreColumnsAllowedForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                EntityActionOperation.Create,
                new List<string> { "col5", "col6" }));
        }

        /// <summary>
        /// Test to validate that for wildcard operation, the authorization stage for column check
        /// would pass if the operation is one among create, read, update, delete and the columns are accessible.
        /// Similarly if the column is in accessible, then we should not have access.
        /// </summary>
        [TestMethod("Explicit include and exclude columns")]
        public void ExplicitIncludeAndExcludeColumns()
        {
            HashSet<string> includeColumns = new() { "col1", "col2" };
            HashSet<string> excludeColumns = new() { "col3" };

            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create,
                includedCols: includeColumns,
                excludedCols: excludeColumns
                );

            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            Assert.IsTrue(authZResolver.AreColumnsAllowedForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                EntityActionOperation.Create,
                includeColumns));

            Assert.IsFalse(authZResolver.AreColumnsAllowedForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                EntityActionOperation.Create,
                excludeColumns));

            // Not exist column in the inclusion or exclusion list
            Assert.IsFalse(authZResolver.AreColumnsAllowedForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                EntityActionOperation.Create,
                new List<string> { "col4" }));

            // Mix of allow and not allow. Should result in not allow.
            Assert.IsFalse(authZResolver.AreColumnsAllowedForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                EntityActionOperation.Create,
                new List<string> { "col1", "col3" }));
        }

        /// <summary>
        /// Exclusion has precedence over inclusion. So for this test case,
        /// col1 will be excluded even if it is in the inclusion list.
        /// </summary>
        [TestMethod("Same column in exclusion and inclusion list")]
        public void ColumnExclusionWithSameColumnInclusion()
        {
            HashSet<string> includedColumns = new() { "col1", "col2" };
            HashSet<string> excludedColumns = new() { "col1", "col4" };

            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create,
                includedCols: includedColumns,
                excludedCols: excludedColumns
                );
            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            // Col2 should be included.
            //
            Assert.IsTrue(authZResolver.AreColumnsAllowedForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create,
                new List<string> { "col2" }));

            // Col1 should NOT to included since it is in exclusion list.
            //
            Assert.IsFalse(authZResolver.AreColumnsAllowedForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create,
                new List<string> { "col1" }));

            Assert.IsFalse(authZResolver.AreColumnsAllowedForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create,
                excludedColumns));
        }

        /// <summary>
        /// Test that wildcard inclusion will include all the columns in the table.
        /// </summary>
        [TestMethod("Wildcard included columns")]
        public void WildcardColumnInclusion()
        {
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create,
                includedCols: new HashSet<string> { AuthorizationResolver.WILDCARD }
                );
            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            List<string> includedColumns = new() { "col1", "col2", "col3", "col4" };

            Assert.IsTrue(authZResolver.AreColumnsAllowedForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create,
                includedColumns));
        }

        /// <summary>
        /// Test that wildcard inclusion will include all column except column specify in exclusion.
        /// Exclusion has priority over inclusion.
        /// </summary>
        [TestMethod("Wildcard include columns with some column exclusion")]
        public void WildcardColumnInclusionWithExplicitExclusion()
        {
            List<string> includedColumns = new() { "col1", "col2" };
            HashSet<string> excludedColumns = new() { "col3", "col4" };

            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create,
                includedCols: new HashSet<string> { AuthorizationResolver.WILDCARD },
                excludedCols: excludedColumns
                );
            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            Assert.IsTrue(authZResolver.AreColumnsAllowedForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create,
                includedColumns));
            Assert.IsFalse(authZResolver.AreColumnsAllowedForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create,
                excludedColumns));
        }

        /// <summary>
        /// Test that all columns should be excluded if the exclusion contains wildcard character.
        /// </summary>
        [TestMethod("Wildcard column exclusion")]
        public void WildcardColumnExclusion()
        {
            HashSet<string> excludedColumns = new() { "col1", "col2", "col3", "col4" };

            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create,
                excludedCols: new HashSet<string> { AuthorizationResolver.WILDCARD }
                );
            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            Assert.IsFalse(authZResolver.AreColumnsAllowedForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create,
                excludedColumns));
        }

        /// <summary>
        /// For this test, exclusion has precedence over inclusion. So all columns will be excluded
        /// because wildcard is specified in the exclusion list.
        /// </summary>
        [TestMethod("Wildcard column exclusion with some explicit columns inclusion")]
        public void WildcardColumnExclusionWithExplicitColumnInclusion()
        {
            HashSet<string> includedColumns = new() { "col1", "col2" };
            HashSet<string> excludedColumns = new() { "col3", "col4" };

            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create,
                includedCols: includedColumns,
                excludedCols: new HashSet<string> { AuthorizationResolver.WILDCARD }
                );
            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            Assert.IsFalse(authZResolver.AreColumnsAllowedForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create,
                includedColumns));
            Assert.IsFalse(authZResolver.AreColumnsAllowedForOperation(
                AuthorizationHelpers.TEST_ENTITY,
                AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create,
                excludedColumns));
        }

        /// <summary>
        /// Test to validate that for wildcard operation, the authorization stage for column check
        /// would pass if the operation is one among create, read, update, delete and the columns are accessible.
        /// Similarly if the column is in accessible, then we should not have access.
        /// </summary>
        [TestMethod("Explicit include and exclude columns with wildcard operation")]
        public void CheckIncludeAndExcludeColumnForWildcardOperation()
        {
            HashSet<string> includeColumns = new() { "col1", "col2" };
            HashSet<string> excludeColumns = new() { "col3" };

            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.All,
                includedCols: includeColumns,
                excludedCols: excludeColumns
                );

            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            foreach (EntityActionOperation operation in EntityAction.ValidPermissionOperations)
            {
                // Validate that the authorization check passes for valid CRUD operations
                // because columns are accessible or inaccessible.
                Assert.IsTrue(authZResolver.AreColumnsAllowedForOperation(
                    AuthorizationHelpers.TEST_ENTITY,
                    AuthorizationHelpers.TEST_ROLE,
                    operation,
                    includeColumns));
                Assert.IsFalse(authZResolver.AreColumnsAllowedForOperation(
                    AuthorizationHelpers.TEST_ENTITY,
                    AuthorizationHelpers.TEST_ROLE,
                    operation,
                    excludeColumns));
            }
        }

        /// <summary>
        /// Test to validate that when Field property is missing from the operation, all the columns present in
        /// the table are treated as accessible. Since we are not explicitly specifying the includeCols/excludedCols
        /// parameters when initializing the RuntimeConfig, Field will be nullified.
        /// </summary>
        [DataTestMethod]
        [DataRow(true, "col1", "col2", DisplayName = "Accessible fields col1,col2")]
        [DataRow(true, "col3", "col4", DisplayName = "Accessible fields col3,col4")]
        [DataRow(false, "col5", DisplayName = "Inaccessible field col5")]
        public void AreColumnsAllowedForOperationWithMissingFieldProperty(bool expected, params string[] columnsToCheck)
        {
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationHelpers.TEST_ROLE,
                operation: EntityActionOperation.Create
            );
            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            // Assert that the expected result and the returned result are equal.
            // The entity is expected to have "col1", "col2", "col3", "col4" fields accessible on it.
            Assert.AreEqual(expected,
                authZResolver.AreColumnsAllowedForOperation(
                    AuthorizationHelpers.TEST_ENTITY,
                    AuthorizationHelpers.TEST_ROLE,
                    EntityActionOperation.Create,
                    new List<string>(columnsToCheck)));
        }

        /// <summary>
        /// Test to validate that the column permissions for authenticated role are derived from anonymous role
        /// when the authenticated role is not defined, but anonymous role is defined.
        /// </summary>
        [DataRow(new string[] { "col1", "col2", "col3" }, new string[] { "col4" },
            new string[] { "col2", "col3" }, true, DisplayName = "fields in include check")]
        [DataRow(new string[] { "col2", "col4" }, new string[] { "col1", "col3" },
            new string[] { "col1", "col4" }, false, DisplayName = "fields in exclude check")]
        [DataRow(new string[] { "col1" }, new string[] { "col2" },
            new string[] { "col2" }, false, DisplayName = "fields in include/exclude mix check")]
        [DataTestMethod]
        public void TestAuthenticatedRoleForColumnPermissionsWhenAnonymousRoleIsDefined(
            string[] includeCols,
            string[] excludeCols,
            string[] columnsToCheck,
            bool expected)
        {
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: AuthorizationResolver.ROLE_ANONYMOUS,
                operation: EntityActionOperation.All,
                includedCols: new HashSet<string>(includeCols),
                excludedCols: new HashSet<string>(excludeCols));

            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            foreach (EntityActionOperation operation in EntityAction.ValidPermissionOperations)
            {
                Assert.AreEqual(expected, authZResolver.AreColumnsAllowedForOperation(
                    AuthorizationHelpers.TEST_ENTITY,
                    AuthorizationResolver.ROLE_AUTHENTICATED,
                    operation,
                    new List<string>(columnsToCheck)));
            }
        }

        /// <summary>
        /// Test to validate the AreColumnsAllowedForOperation method for case insensitivity of roleName.
        /// For eg. The role CREATOR is equivalent to creator, cReAtOR etc.
        /// </summary>
        /// <param name="operation">The operation configured on the entity.</param>
        /// <param name="configRole">The role configured on the entity.</param>
        /// <param name="columnsToInclude">Columns accessible for the given role and operation.</param>
        /// <param name="columnsToExclude">Columns inaccessible for the given role and operation.</param>
        /// <param name="roleName">The roleName to be tested, differs in casing with configRole.</param>
        /// <param name="columnsToCheck">Columns to be checked for access.</param>
        /// <param name="expected">Expected boolean result for the relevant method call.</param>
        [DataTestMethod]
        [DataRow(EntityActionOperation.All, "Writer", new string[] { "col1", "col2" }, new string[] { "col3" }, "WRITER",
            new string[] { "col1", "col2" }, true, DisplayName = "Case insensitive role writer")]
        [DataRow(EntityActionOperation.Read, "Reader", new string[] { "col1", "col3", "col4" }, new string[] { "col3" }, "reADeR",
            new string[] { "col1", "col3" }, false, DisplayName = "Case insensitive role reader")]
        [DataRow(EntityActionOperation.Create, "Creator", new string[] { "col1", "col2" }, new string[] { "col3", "col4" }, "CREator",
            new string[] { "col1", "col2" }, true, DisplayName = "Case insensitive role creator")]
        public void AreColumnsAllowedForOperationWithRoleWithDifferentCasing(
            EntityActionOperation operation,
            string configRole,
            string[] columnsToInclude,
            string[] columnsToExclude,
            string roleName,
            string[] columnsToCheck,
            bool expected)
        {
            RuntimeConfig runtimeConfig = AuthorizationHelpers.InitRuntimeConfig(
                entityName: AuthorizationHelpers.TEST_ENTITY,
                roleName: configRole,
                operation: operation,
                includedCols: new(columnsToInclude),
                excludedCols: new(columnsToExclude)
                );
            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            List<EntityActionOperation> operations = AuthorizationResolver.GetAllOperationsForObjectType(operation, EntitySourceType.Table).ToList();

            foreach (EntityActionOperation testOperation in operations)
            {
                // Assert that the expected result and the returned result are equal.
                Assert.AreEqual(expected,
                    authZResolver.AreColumnsAllowedForOperation(
                        AuthorizationHelpers.TEST_ENTITY,
                        roleName,
                        testOperation,
                        new List<string>(columnsToCheck)));
            }
        }
        #endregion

        #region Tests to validate Database policy parsing
        /// <summary>
        /// Validates the policy parsing logic by asserting that the parsed policy matches the expectedParsedPolicy.
        /// </summary>
        /// <param name="policy">The policy to be parsed.</param>
        /// <param name="expectedParsedPolicy">The policy which is expected to be generated after parsing.</param>
        [DataTestMethod]
        [DataRow("@claims.user_email ne @item.col1 and @claims.contact_no eq @item.col2 and not(@claims.name eq @item.col3)",
            "'xyz@microsoft.com' ne col1 and 1234 eq col2 and not('Aaron' eq col3)",
            DisplayName = "Valid policy parsing test for string and int64 claimvaluetypes.")]
        [DataRow("(@claims.isemployee eq @item.col1 and @item.col2 ne @claims.user_email) or" +
            "('David' ne @item.col3 and @claims.contact_no ne @item.col3)", "(true eq col1 and col2 ne 'xyz@microsoft.com') or" +
            "('David' ne col3 and 1234 ne col3)", DisplayName = "Valid policy parsing test for constant string and int64 claimvaluetype.")]
        [DataRow("(@item.rating gt @claims.emprating) and (@claims.isemployee eq true)",
            "(rating gt 4.2) and (true eq true)", DisplayName = "Valid policy parsing test for double and boolean claimvaluetypes.")]
        [DataRow("@item.rating eq @claims.emprating)", "rating eq 4.2)", DisplayName = "Valid policy parsing test for double claimvaluetype.")]
        public void ParseValidDbPolicy(string policy, string expectedParsedPolicy)
        {
            RuntimeConfig runtimeConfig = InitRuntimeConfig(
                entityName: TEST_ENTITY,
                roleName: TEST_ROLE,
                operation: TEST_OPERATION,
                includedCols: new HashSet<string> { "col1", "col2", "col3" },
                databasePolicy: policy
                );
            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            Mock<HttpContext> context = new();

            ClaimsIdentity identity = new(TEST_AUTHENTICATION_TYPE, TEST_CLAIMTYPE_NAME, AuthenticationOptions.ROLE_CLAIM_TYPE);
            identity.AddClaim(new Claim("user_email", "xyz@microsoft.com", ClaimValueTypes.String));
            identity.AddClaim(new Claim("name", "Aaron", ClaimValueTypes.String));
            identity.AddClaim(new Claim("contact_no", "1234", ClaimValueTypes.Integer64));
            identity.AddClaim(new Claim("isemployee", "true", ClaimValueTypes.Boolean));
            identity.AddClaim(new Claim("emprating", "4.2", ClaimValueTypes.Double));
            ClaimsPrincipal principal = new(identity);
            context.Setup(x => x.User).Returns(principal);
            context.Setup(x => x.Request.Headers[AuthorizationResolver.CLIENT_ROLE_HEADER]).Returns(TEST_ROLE);

            string parsedPolicy = authZResolver.ProcessDBPolicy(TEST_ENTITY, TEST_ROLE, TEST_OPERATION, context.Object);
            Assert.AreEqual(parsedPolicy, expectedParsedPolicy);
        }

        /// <summary>
        /// Tests authorization policy processing mechanism by validating value type compatibility
        /// of claims present in HttpContext.User.Claims.
        /// </summary>
        /// <param name="claimValueType">Claim.ValueType which is a string, by definition.</param>
        /// <param name="claimValue">Claim.Value which is a string, by definition.</param>
        /// <param name="supportedValueType">Whether Claim.ValueType is supported by DAB engine</param>
        /// <seealso cref="https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet/blob/9ddad8fc51ed2732622323612acad83f6629d5ba/src/Microsoft.IdentityModel.JsonWebTokens/Json/JsonClaimSet.cs#L76-L124"/>
        /// <seealso cref="https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet/blob/59d1307a260829c0f8609a183a962aceaeffba89/src/Microsoft.IdentityModel.Tokens/TokenUtilities.cs#L82-L112"/>
        #pragma warning disable format
        [DataTestMethod]
        [DataRow(ClaimValueTypes.String,        "StringLiteral",                      true, DisplayName = "string")]
        [DataRow(ClaimValueTypes.Boolean,       "true",                               true, DisplayName = "bool")]
        [DataRow(ClaimValueTypes.Integer,       "65535",                              true, DisplayName = "short")]
        [DataRow(ClaimValueTypes.Integer,       "-2147483648",                        true, DisplayName = "int - Scenario 1")]
        [DataRow(ClaimValueTypes.Integer32,     "2147483647",                         true, DisplayName = "int - Scenario 2")]
        [DataRow(ClaimValueTypes.Integer64,     "9223372036854775807",                true, DisplayName = "long")]
        [DataRow(ClaimValueTypes.UInteger32,    "4294967295",                         true, DisplayName = "uint")]
        [DataRow(ClaimValueTypes.UInteger64,    "18446744073709551615",               true, DisplayName = "ulong")]
        [DataRow(ClaimValueTypes.Double,        "12.34",                              true, DisplayName = "decimal")]
        [DataRow(ClaimValueTypes.Double,        "12.345",                             true, DisplayName = "double")]
        [DataRow(JsonClaimValueTypes.JsonNull,  "null",                               true, DisplayName = "Json null literal")]
        [DataRow(ClaimValueTypes.DateTime,      "2022-11-30T22:57:57.5847834Z",       false, DisplayName = "DateTime")]
        [DataRow(JsonClaimValueTypes.Json,      "{\"\"ext1\"\":\"\"ext1Value\"\"}",   false, DisplayName = "Json object")]
        [DataRow(JsonClaimValueTypes.JsonArray, "[{\"\"ext1\"\":\"\"ext1Value\"\"}]", false, DisplayName = "Json array")]
        #pragma warning restore format
        public void DbPolicy_ClaimValueTypeParsing(string claimValueType, string claimValue, bool supportedValueType)
        {
            // To adhere with OData 4 ABNF construction rules (Section 7: Literal Data Values)
            // - Primitive string literals in URLS must be enclosed within single quotes.
            // - http://docs.oasis-open.org/odata/odata/v4.01/cs01/abnf/odata-abnf-construction-rules.txt
            string odataClaimValue = (claimValueType == ClaimValueTypes.String) ? "'" + claimValue + "'" : claimValue;
            string expectedPolicy = odataClaimValue + " eq col1";
            string policyDefinition = "@claims.testClaim eq @item.col1";

            RuntimeConfig runtimeConfig = InitRuntimeConfig(
                entityName: TEST_ENTITY,
                roleName: TEST_ROLE,
                operation: TEST_OPERATION,
                includedCols: new HashSet<string> { "col1" },
                databasePolicy: policyDefinition);

            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            Mock<HttpContext> context = new();

            //Add identity to the request context.
            ClaimsIdentity identity = new(TEST_AUTHENTICATION_TYPE, TEST_CLAIMTYPE_NAME, AuthenticationOptions.ROLE_CLAIM_TYPE);
            identity.AddClaim(new Claim("testClaim", claimValue, claimValueType));

            ClaimsPrincipal principal = new(identity);
            context.Setup(x => x.User).Returns(principal);
            context.Setup(x => x.Request.Headers[AuthorizationResolver.CLIENT_ROLE_HEADER]).Returns(TEST_ROLE);

            try
            {
                string parsedPolicy = authZResolver.ProcessDBPolicy(TEST_ENTITY, TEST_ROLE, TEST_OPERATION, context.Object);
                Assert.IsTrue(supportedValueType);
                Assert.AreEqual(expectedPolicy, parsedPolicy);
            }
            catch (DataApiBuilderException ex)
            {
                Assert.IsFalse(supportedValueType, message: ex.Message);
                Assert.AreEqual(
                    $"The claim value for claim: testClaim belonging to the user has an unsupported data type.",
                    actual: ex.Message,
                    message: "Test expected to fail- a claim value for claim belonging to the user had datatype " +
                    "which is not currently supported by DAB.");
            }
        }

        /// <summary>
        /// Test to validate that we are correctly throwing an appropriate exception when the user request
        /// lacks a claim required by the policy.
        /// </summary>
        /// <param name="policy">The policy to be parsed.</param>
        [DataTestMethod]
        [DataRow("@claims.user_email eq @item.col1 and @claims.emprating eq @item.rating",
            DisplayName = "'emprating' claim missing from request")]
        [DataRow("@claims.user_email eq @item.col1 and not ( true eq @claims.isemployee or @claims.name eq 'Aaron')",
            DisplayName = "'name' claim missing from request")]
        public void ParseInvalidDbPolicyWithUserNotPossessingAllClaims(string policy)
        {
            RuntimeConfig runtimeConfig = InitRuntimeConfig(
                entityName: TEST_ENTITY,
                roleName: TEST_ROLE,
                operation: TEST_OPERATION,
                includedCols: new HashSet<string> { "col1", "col2", "col3" },
                databasePolicy: policy
                );
            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            Mock<HttpContext> context = new();

            //Add identity to the readAction, updateAction
            ClaimsIdentity identity = new(TEST_AUTHENTICATION_TYPE, TEST_CLAIMTYPE_NAME, AuthenticationOptions.ROLE_CLAIM_TYPE);
            identity.AddClaim(new Claim("user_email", "xyz@microsoft.com", ClaimValueTypes.String));
            identity.AddClaim(new Claim("isemployee", "true", ClaimValueTypes.Boolean));
            ClaimsPrincipal principal = new(identity);
            context.Setup(x => x.User).Returns(principal);
            context.Setup(x => x.Request.Headers[AuthorizationResolver.CLIENT_ROLE_HEADER]).Returns(TEST_ROLE);

            try
            {
                authZResolver.ProcessDBPolicy(TEST_ENTITY, TEST_ROLE, TEST_OPERATION, context.Object);
            }
            catch (DataApiBuilderException ex)
            {
                Assert.AreEqual(HttpStatusCode.Forbidden, ex.StatusCode);
                Assert.AreEqual("User does not possess all the claims required to perform this operation.", ex.Message);
            }
        }

        /// <summary>
        /// Test to validate that duplicate claims throws an exception for everything except roles
        /// duplicate role claims are ignored, so just checks policy is parsed as expected in this case 
        /// </summary>
        /// <param name="exceptionExpected"> Whether we expect an exception (403 forbidden) to be thrown while parsing policy </param>
        /// <param name="claimTypes"> Parameter list of claim types/keys to add to the claims dictionary that can be accessed with @claims </param>
        [DataTestMethod]
        [DataRow(true, AuthenticationOptions.ROLE_CLAIM_TYPE, "username", "guid", "username",
            DisplayName = "duplicate claim expect exception")]
        [DataRow(false, AuthenticationOptions.ROLE_CLAIM_TYPE, "username", "guid", AuthenticationOptions.ROLE_CLAIM_TYPE,
            DisplayName = "duplicate role claim does not expect exception")]
        [DataRow(true, AuthenticationOptions.ROLE_CLAIM_TYPE, AuthenticationOptions.ROLE_CLAIM_TYPE, "username", "username",
            DisplayName = "duplicate claim expect exception ignoring role")]
        public void ParsePolicyWithDuplicateUserClaims(bool exceptionExpected, params string[] claimTypes)
        {
            string policy = $"@claims.guid eq 1";
            string defaultClaimValue = "unimportant";
            RuntimeConfig runtimeConfig = InitRuntimeConfig(
                entityName: TEST_ENTITY,
                roleName: TEST_ROLE,
                operation: TEST_OPERATION,
                includedCols: new HashSet<string> { "col1", "col2", "col3" },
                databasePolicy: policy
                );
            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);
            Mock<HttpContext> context = new();

            // Add identity to the readAction, updateAction.
            ClaimsIdentity identity = new(TEST_AUTHENTICATION_TYPE, TEST_CLAIMTYPE_NAME, AuthenticationOptions.ROLE_CLAIM_TYPE);
            foreach (string claimType in claimTypes)
            {
                identity.AddClaim(new Claim(type: claimType, value: defaultClaimValue, ClaimValueTypes.String));
            }

            ClaimsPrincipal principal = new(identity);
            context.Setup(x => x.User).Returns(principal);
            context.Setup(x => x.Request.Headers[AuthorizationResolver.CLIENT_ROLE_HEADER]).Returns(TEST_ROLE);

            // We expect an exception if duplicate claims are present EXCEPT for role claim
            if (exceptionExpected)
            {
                try
                {
                    authZResolver.ProcessDBPolicy(TEST_ENTITY, TEST_ROLE, TEST_OPERATION, context.Object);
                    Assert.Fail();
                }
                catch (DataApiBuilderException ex)
                {
                    Assert.AreEqual(HttpStatusCode.Forbidden, ex.StatusCode);
                    Assert.AreEqual("Duplicate claims are not allowed within a request.", ex.Message);
                }
            }
            else
            {
                // If the role claim was the only duplicate, simply verify policy parsed as expected
                string expectedPolicy = $"'{defaultClaimValue}' eq 1";
                string parsedPolicy = authZResolver.ProcessDBPolicy(TEST_ENTITY, TEST_ROLE, TEST_OPERATION, context.Object);
                Assert.AreEqual(expected: expectedPolicy, actual: parsedPolicy);
            }
        }

        // Indirectly tests the AuthorizationResolver private method:
        // GetDBPolicyForRequest(string entityName, string roleName, string operation)
        // by calling public method TryProcessDBPolicy(TEST_ENTITY, clientRole, requestOperation, context.Object)
        // The result of executing that method will determine whether execution behaves as expected.
        // When string.Empty is returned,
        // then no policy is found for the provided entity, role, and operation combination, therefore,
        // no predicates need to be added to the database query generated for the request.
        // When a value is returned as a result, the execution behaved as expected.
        [DataTestMethod]
        [DataRow("anonymous", "anonymous", EntityActionOperation.Read, EntityActionOperation.Read, "id eq 1", true,
            DisplayName = "Fetch Policy for existing system role - anonymous")]
        [DataRow("authenticated", "authenticated", EntityActionOperation.Update, EntityActionOperation.Update, "id eq 1", true,
            DisplayName = "Fetch Policy for existing system role - authenticated")]
        [DataRow("anonymous", "anonymous", EntityActionOperation.Read, EntityActionOperation.Read, null, false,
            DisplayName = "Fetch Policy for existing role, no policy object defined in config.")]
        [DataRow("anonymous", "authenticated", EntityActionOperation.Read, EntityActionOperation.Read, "id eq 1", false,
            DisplayName = "Fetch Policy for non-configured role")]
        [DataRow("anonymous", "anonymous", EntityActionOperation.Read, EntityActionOperation.Create, "id eq 1", false,
            DisplayName = "Fetch Policy for non-configured operation")]
        public void GetDBPolicyTest(
            string clientRole,
            string configuredRole,
            EntityActionOperation requestOperation,
            EntityActionOperation configuredOperation,
            string policy,
            bool expectPolicy)
        {
            RuntimeConfig runtimeConfig = InitRuntimeConfig(
                entityName: TEST_ENTITY,
                roleName: configuredRole,
                operation: configuredOperation,
                databasePolicy: policy
                );

            AuthorizationResolver authZResolver = AuthorizationHelpers.InitAuthorizationResolver(runtimeConfig);

            Mock<HttpContext> context = new();

            // Add identity to the readAction, updateAction.
            ClaimsIdentity identity = new(TEST_AUTHENTICATION_TYPE, TEST_CLAIMTYPE_NAME, AuthenticationOptions.ROLE_CLAIM_TYPE);
            identity.AddClaim(new Claim("user_email", "xyz@microsoft.com", ClaimValueTypes.String));
            ClaimsPrincipal principal = new(identity);
            context.Setup(x => x.User).Returns(principal);
            context.Setup(x => x.Request.Headers[AuthorizationResolver.CLIENT_ROLE_HEADER]).Returns(clientRole);

            string parsedPolicy = authZResolver.ProcessDBPolicy(TEST_ENTITY, clientRole, requestOperation, context.Object);
            string errorMessage = "TryProcessDBPolicy returned unexpected value.";
            if (expectPolicy)
            {
                Assert.AreEqual(actual: parsedPolicy, expected: policy, message: errorMessage);
            }
            else
            {
                Assert.AreEqual(actual: parsedPolicy, expected: string.Empty, message: errorMessage);
            }
        }

        /// <summary>
        /// Test to validate the AuthorizationResolver.GetAllUserClaims() successfully adds role claim to the claimsInRequestContext dictionary.
        /// Only the role claim corresponding to the X-MS-API-ROLE header is added to the claimsInRequestContext.
        /// The role claim will be sourced by DAB when the user is not already a member of a system role(authenticated/anonymous),
        /// or the role claim will be sourced from a user's access token issued by an identity provider.
        /// </summary>
        [TestMethod]
        public void ValidateClientRoleHeaderClaimIsAddedToClaimsInRequestContext()
        {
            Mock<HttpContext> context = new();

            //Add identity object to the Mock context object.
            ClaimsIdentity identityWithClientRoleHeaderClaim = new(TEST_AUTHENTICATION_TYPE, TEST_CLAIMTYPE_NAME, AuthenticationOptions.ROLE_CLAIM_TYPE);
            Claim clientRoleHeaderClaim = new(AuthenticationOptions.ROLE_CLAIM_TYPE, TEST_ROLE);
            identityWithClientRoleHeaderClaim.AddClaim(clientRoleHeaderClaim);

            // Add identity object with role claim which is not equal to the clientRoleHeader.
            ClaimsIdentity identityWithoutClientRoleHeaderClaim = new(TEST_AUTHENTICATION_TYPE, TEST_CLAIMTYPE_NAME, AuthenticationOptions.ROLE_CLAIM_TYPE);
            Claim readerRoleClaim = new(AuthenticationOptions.ROLE_CLAIM_TYPE, "Reader");
            identityWithClientRoleHeaderClaim.AddClaim(readerRoleClaim);

            ClaimsPrincipal principal = new();
            principal.AddIdentity(identityWithoutClientRoleHeaderClaim);
            principal.AddIdentity(identityWithClientRoleHeaderClaim);

            context.Setup(x => x.User).Returns(principal);
            context.Setup(x => x.Request.Headers[AuthorizationResolver.CLIENT_ROLE_HEADER]).Returns(TEST_ROLE);

            // Execute the method to be tested - GetAllUserClaims().
            Dictionary<string, Claim> claimsInRequestContext = AuthorizationResolver.GetAllUserClaims(context.Object);

            // Assert that only the role claim corresponding to clientRoleHeader is added to the claims dictionary.
            Assert.IsTrue(claimsInRequestContext.Count == 1);
            Assert.IsTrue(claimsInRequestContext.ContainsKey(AuthenticationOptions.ROLE_CLAIM_TYPE));
            Assert.IsTrue(TEST_ROLE.Equals(claimsInRequestContext[AuthenticationOptions.ROLE_CLAIM_TYPE].Value));
        }
        #endregion

        #region Helpers
        /// <summary>
        /// Creates code-first in-memory RuntimeConfig.
        /// </summary>
        /// <param name="entityName">Name of the entity.</param>
        /// <param name="roleName">Role permitted to access entity.</param>
        /// <param name="operation">Operation to allow on role</param>
        /// <param name="includedCols">Allowed columns to access for operation defined on role.</param>
        /// <param name="excludedCols">Excluded columns to access for operation defined on role.</param>
        /// <param name="requestPolicy">Request authorization policy. (Support TBD)</param>
        /// <param name="databasePolicy">Database authorization policy.</param>
        /// <returns>Mocked RuntimeConfig containing metadata provided in method arguments.</returns>
        public static RuntimeConfig InitRuntimeConfig(
            string entityName = "SampleEntity",
            string roleName = "Reader",
            EntityActionOperation operation = EntityActionOperation.Create,
            HashSet<string>? includedCols = null,
            HashSet<string>? excludedCols = null,
            string? requestPolicy = null,
            string? databasePolicy = null
            )
        {
            EntityActionFields fieldsForRole = new(
                Include: includedCols,
                Exclude: excludedCols ?? new());

            EntityActionPolicy policy = new(
                    Request: requestPolicy,
                    Database: databasePolicy);

            EntityAction operationForRole = new(
                Action: operation,
                Fields: fieldsForRole,
                Policy: policy);

            EntityPermission permissionForEntity = new(
                Role: roleName,
                Actions: new[] { operationForRole });

            return BuildTestRuntimeConfig(new[] { permissionForEntity }, entityName);
        }

        private static RuntimeConfig BuildTestRuntimeConfig(EntityPermission[] permissions, string entityName)
        {
            Entity sampleEntity = new(
                Source: new(entityName, EntitySourceType.Table, null, null),
                Rest: new(Array.Empty<SupportedHttpVerb>()),
                GraphQL: new("", ""),
                Permissions: permissions,
                Relationships: null,
                Mappings: null);

            Dictionary<string, Entity> entityMap = new()
            {
                { entityName, sampleEntity }
            };

            RuntimeConfig runtimeConfig = new(
                Schema: "UnitTestSchema",
                DataSource: new DataSource(DatabaseType: DatabaseType.MSSQL, "", new()),
                Runtime: new(
                    Rest: new(),
                    GraphQL: new(),
                    Host: new(null, null)
                ),
                Entities: new(entityMap)
            );
            return runtimeConfig;
        }
        #endregion
    }
}
