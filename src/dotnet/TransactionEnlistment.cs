// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Transactions;

namespace Microsoft.DotNet.Cli
{
    public sealed class TransactionEnlistment : IEnlistmentNotification
    {
        private Action _commit;
        private Action _rollback;

        public static void Enlist(Action commit = null, Action rollback = null)
        {
            if (Transaction.Current == null)
            {
                throw new InvalidOperationException();
            }

            if (commit == null && rollback == null)
            {
                return;
            }

            Transaction.Current.EnlistVolatile(
                new TransactionEnlistment(commit, rollback),
                EnlistmentOptions.None);
        }

        void IEnlistmentNotification.Commit(Enlistment enlistment)
        {
            if (_commit != null)
            {
                _commit();
                _commit = null;
            }
            enlistment.Done();
        }

        void IEnlistmentNotification.InDoubt(Enlistment enlistment)
        {
            ((IEnlistmentNotification)this).Rollback(enlistment);
        }

        void IEnlistmentNotification.Prepare(PreparingEnlistment enlistment)
        {
            enlistment.Prepared();
        }

        void IEnlistmentNotification.Rollback(Enlistment enlistment)
        {
            if (_rollback != null)
            {
                _rollback();
                _rollback = null;
            }

            enlistment.Done();
        }

        private TransactionEnlistment(Action commit, Action rollback)
        {
            _commit = commit;
            _rollback = rollback;
        }
    }
}
